using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using ErogeLauncher.Models;
using HtmlAgilityPack;

namespace ErogeLauncher.Services;
/// <summary>批評空間への書き込み側（ログイン、得点登録フォームの読み込みと送信）</summary>
public partial class EgsClient
{
    // ================================================================
    // ログイン
    // ================================================================

    public async Task LoginAsync(string user, string password)
    {
        if (!EgsConfig.IsSet(_cfg.LoginPage))
            throw new EgsException("egs_config.json の LoginPage が未設定です。");

        Logout(); // 前のログイン状態（別のIDなど）を持ち越さない

        var url = _cfg.Url(_cfg.LoginPage);
        var doc = Load(await GetAsync(url));

        // パスワード欄を含むフォームをログインフォームとみなす
        var form = doc.DocumentNode.SelectSingleNode("//form[.//input[@type='password']]")
                   ?? throw new EgsException("ログインフォームが見つかりませんでした。");
        var fields = ReadForm(form);

        var pwName = form.SelectSingleNode(".//input[@type='password']")!.GetAttributeValue("name", "");
        var userName = form.SelectNodes(".//input[@type='text' or @type='email' or not(@type)]")
                           ?.FirstOrDefault()?.GetAttributeValue("name", "")
                       ?? throw new EgsException("ログインフォームのID欄が見つかりませんでした。");

        SetField(fields, userName, user);
        SetField(fields, pwName, password);

        // ---- パスワードの送り先を確認する（批評空間以外・暗号化されない通信・URLに載る送り方には送らない） ----
        // 批評空間かどうかは、設定の BaseUrl ではなく決まったホストで判断する（設定を書き換えられても、送り先を変えられないように）
        var action = HtmlEntity.DeEntitize(form.GetAttributeValue("action", ""));
        var actionUri = string.IsNullOrEmpty(action) ? new Uri(url) : new Uri(new Uri(url), action);
        if (actionUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(actionUri.Host, EgsConfig.OfficialHost, StringComparison.OrdinalIgnoreCase) ||
            !form.GetAttributeValue("method", "get").Equals("post", StringComparison.OrdinalIgnoreCase))
            throw new EgsException("ログインフォームの送り先が想定と違うため、パスワードを送信しませんでした。\n" + actionUri);

        var resultHtml = await SubmitFormAsync(form, url, fields);
        LoggedIn = resultHtml.Contains(_cfg.LoggedInMarker);

        string? topHtml = null;
        if (!LoggedIn)
        {
            // ログイン直後のページが「移動中…」だけのこともあるので、トップページでも確認する
            topHtml = await GetAsync(_cfg.BaseUrl);
            LoggedIn = topHtml.Contains(_cfg.LoggedInMarker);
        }

        if (LoggedIn)
        {
            DeleteDebugFiles(); // 成功したら、前回の調査用ファイルは不要
            return;
        }

        // ---- 失敗したときだけ、調査用ファイルを保存する（パスワードと、隠し項目の値は伏せる） ----
        try
        {
            var hidden = new HashSet<string>(
                form.SelectNodes(".//input[@type='hidden']")?.Select(n => n.GetAttributeValue("name", "")) ?? Enumerable.Empty<string>());
            var formInfo = new StringBuilder();
            formInfo.AppendLine($"ページURL : {url}");
            formInfo.AppendLine($"action    : {action}");
            formInfo.AppendLine($"method    : {form.GetAttributeValue("method", "(なし)")}");
            formInfo.AppendLine($"ID欄の name       : {userName}");
            formInfo.AppendLine($"パスワード欄の name : {pwName}");
            formInfo.AppendLine("送信した項目:");
            foreach (var f in fields)
                formInfo.AppendLine($"  {f.Key} = {(f.Key == pwName || hidden.Contains(f.Key) ? "********" : f.Value)}");
            File.WriteAllText(Path.Combine(AppPaths.Dir, "debug_login_form.txt"), formInfo.ToString());
            File.WriteAllText(Path.Combine(AppPaths.Dir, "debug_login_result.html"), resultHtml);
            if (topHtml != null) File.WriteAllText(Path.Combine(AppPaths.Dir, "debug_login_top.html"), topHtml);
        }
        catch (Exception ex) { Log.Warn("調査用ファイルを書けませんでした", ex); } // 書けなくても、ログイン失敗の知らせは出す

        throw new EgsException(
            "ログインに失敗しました。ID・パスワード、または LoggedInMarker の設定を確認してください。\n" +
            $"調査用ファイルを保存しました（ログインに成功すると自動で消えます）：{AppPaths.Dir}");
    }

    private async Task EnsureLoginAsync()
    {
        if (LoggedIn) return;
        var cred = CredentialStore.Load()
                   ?? throw new EgsException("ログイン情報が未設定です。「ログイン設定」から登録してください。");
        await LoginAsync(cred.User, cred.Password);
    }

    // ================================================================
    // 書き込み（得点登録フォーム）
    // ================================================================

    public string ReviewFormUrl(int egsId) =>
        EgsConfig.IsSet(_cfg.ReviewFormUrl) ? _cfg.Url(_cfg.ReviewFormUrl, egsId) : GamePageUrl(egsId);

    public string GamePageUrl(int egsId) => _cfg.Url(_cfg.GamePageUrl, egsId);

    /// <summary>
    /// 得点登録フォームの「プレイ済」・クリア日・得点だけを書き換えて送信する。
    /// 得点や感想など他の項目は、フォームに入っている今の値をそのまま送り返す。
    /// </summary>
    /// <param name="played">null のときはプレイ済の☑を今のまま変えない</param>
    /// <param name="score">null のときは得点を今のまま変えない（clearScore を指定しない限り、空にはしない）</param>
    /// <param name="clearScore">得点を空にして送る（ツールで得点を空欄にしたことを反映するとき）</param>
    /// <param name="extra">そのほかに書き換える項目（フォームの name → 値。値が null なら☑を外す）</param>
    public async Task UpdateReviewAsync(int egsId, bool? played, DateTime? clearDate, int? score,
        IReadOnlyDictionary<string, string?>? extra = null, bool clearScore = false)
    {
        if (!EgsConfig.IsSet(_cfg.ReviewFormUrl) || !EgsConfig.IsSet(_cfg.PlayedField))
            throw new EgsException("egs_config.json の ReviewFormUrl / PlayedField が未設定です。");

        await EnsureLoginAsync();
        var url = ReviewFormUrl(egsId);

        var form = await FindReviewFormAsync(url);
        if (form == null)
        {
            // ログインが切れている可能性があるので、1回だけログインし直して再挑戦
            LoggedIn = false;
            await EnsureLoginAsync();
            form = await FindReviewFormAsync(url)
                   ?? throw new EgsException("得点登録フォームが見つかりませんでした（フォームの仕様が変わった可能性があります）。");
        }

        var fields = ReadForm(form);

        // 発売前の作品のページには、プレイ済・得点の欄がない「基本情報」だけのフォーム（購入予定・購入前得点）が出る。
        // その場合は、プレイ済・クリア日・得点には触れず、フォームにある欄だけを書き換える
        var hasPlayedField = form.SelectSingleNode($".//*[@name='{_cfg.PlayedField}']") != null;

        if (hasPlayedField)
        {
            // ---- プレイ済（null なら今の状態のまま） ----
            // 同じ名前の隠し項目（外したときの「0」）が先にあっても、☑を付けるときの値はチェック欄から読む
            var playedInput = form.SelectSingleNode($".//input[@name='{_cfg.PlayedField}' and @type='checkbox']")
                              ?? form.SelectSingleNode($".//*[@name='{_cfg.PlayedField}']")!;
            if (played == true)
            {
                var value = _cfg.PlayedValue != "" ? _cfg.PlayedValue : playedInput.GetAttributeValue("value", "on");
                SetField(fields, _cfg.PlayedField, value);
            }
            else if (played == false)
            {
                UncheckField(form, fields, _cfg.PlayedField);
            }

            // ---- 得点（入力があれば書き換える。空にするのは、clearScore を指定したときだけ） ----
            // 批評空間は、得点が入っていると「プレイ済」を外せない。未プレイに戻すときは、得点も空にして送る必要がある
            if (EgsConfig.IsSet(_cfg.ScoreField))
            {
                if (score is int sc) SetField(fields, _cfg.ScoreField, sc.ToString());
                else if (clearScore) SetField(fields, _cfg.ScoreField, "");
            }

            // ---- クリア日 ----
            // 日付があれば書き換える。「プレイ済」を外すときは日付も空にする
            // （批評空間側に日付が残っていると、その日付がそのまま再送信されて、プレイ済が外れないため）
            if (clearDate != null || played == false)
                SetClearDate(fields, clearDate);
        }

        // ---- そのほかの項目（所持・感想など。変更したものだけ） ----
        if (extra != null)
        {
            foreach (var (name, value) in extra)
            {
                // 発売前のフォームにない欄は送らない（送っても反映されず、確認でも「未反映」のまま残る）
                if (!hasPlayedField && form.SelectSingleNode($".//*[@name='{name}']") == null) continue;
                if (value == null) UncheckField(form, fields, name);
                else SetField(fields, name, value);
            }
        }

        // 反映が効かないときの調べ用に、プレイ済・クリア日まわりの送信内容だけを記録する（感想などの文章は記録しない）
        var playRelated = fields.Where(f => f.Key == _cfg.PlayedField || f.Key == _cfg.ClearDateField || f.Key.Contains("play", StringComparison.OrdinalIgnoreCase) ||
                                            f.Key.Contains("finish", StringComparison.OrdinalIgnoreCase) || f.Key.Contains("giveup", StringComparison.OrdinalIgnoreCase))
            .Select(f => $"{f.Key}={f.Value}");
        Log.Info($"批評空間へ送信 game={egsId} played引数={played?.ToString() ?? "変更なし"} 送信項目: {string.Join(", ", playRelated)} ／ フォームの欄: {DescribePlayInputs(form)}");
        await SubmitFormAsync(form, url, fields);
    }

    /// <summary>
    /// チェックボックスの☑を外す。ブラウザと同じく、チェック欄そのものは送らない。
    /// ただし、同じ名前の隠し項目（外したときの値「0」を送るためのもの）は、ブラウザが送るのと同じように残す。
    /// これを消してしまうと、サイト側は「変更なし」と受け取り、☑が外れないことがある。
    /// </summary>
    internal static void UncheckField(HtmlNode form, List<KeyValuePair<string, string>> fields, string name)
    {
        fields.RemoveAll(f => f.Key == name);
        foreach (var n in form.SelectNodes($".//input[@name='{name}']") ?? Enumerable.Empty<HtmlNode>())
            if (n.GetAttributeValue("type", "").Equals("hidden", StringComparison.OrdinalIgnoreCase) && !n.Attributes.Contains("disabled"))
                fields.Add(new(name, Attr(n, "value")));
    }

    /// <summary>反応を調べるための記録：プレイ済・クリア日などの欄の種類と値（感想などの文章は記録しない）</summary>
    private static string DescribePlayInputs(HtmlNode form) => string.Join(" / ",
        (form.SelectNodes(".//input|.//select") ?? Enumerable.Empty<HtmlNode>())
        .Where(n => Regex.IsMatch(n.GetAttributeValue("name", ""), "play|finish|giveup|possession", RegexOptions.IgnoreCase))
        .Select(n => $"{n.GetAttributeValue("name", "")}[{n.Name}:{n.GetAttributeValue("type", "")}]={Attr(n, "value")}{(n.Attributes.Contains("checked") ? " checked" : "")}"));

    /// <summary>フォームのクリア日の欄を書き換える（date が null なら空にする）</summary>
    internal void SetClearDate(List<KeyValuePair<string, string>> fields, DateTime? date)
    {
        void Set(string name, string? value)
        {
            if (EgsConfig.IsSet(name)) SetField(fields, name, value ?? "");
        }
        Set(_cfg.ClearDateField, date?.ToString(_cfg.ClearDateFormat, CultureInfo.InvariantCulture));
        Set(_cfg.ClearDateYearField, date?.Year.ToString());
        Set(_cfg.ClearDateMonthField, date?.Month.ToString());
        Set(_cfg.ClearDateDayField, date?.Day.ToString());
    }

    private async Task<HtmlNode?> FindReviewFormAsync(string url)
    {
        var doc = Load(await GetAsync(url));
        var forms = doc.DocumentNode.SelectNodes("//form");
        return forms?.FirstOrDefault(f => f.SelectSingleNode($".//*[@name='{_cfg.PlayedField}']") != null)
               // 発売前の作品：プレイ済の欄がなく、購入予定の欄があるフォーム
               ?? forms?.FirstOrDefault(f => f.SelectSingleNode($".//*[@name='{_cfg.ReviewFormField("purchase_will")}']") != null);
    }

    // ================================================================
    // フォーム操作の共通処理
    // ================================================================

    /// <summary>ブラウザが送信するのと同じように、フォームの今の値をすべて読み取る</summary>
    private static List<KeyValuePair<string, string>> ReadForm(HtmlNode form)
    {
        var list = new List<KeyValuePair<string, string>>();
        var submitAdded = false;

        foreach (var n in form.SelectNodes(".//input|.//select|.//textarea|.//button")
                          ?? Enumerable.Empty<HtmlNode>())
        {
            var name = n.GetAttributeValue("name", "");
            if (name == "" || n.Attributes.Contains("disabled")) continue;

            switch (n.Name)
            {
                case "input":
                    var type = n.GetAttributeValue("type", "text").ToLowerInvariant();
                    if (type is "button" or "reset" or "file" or "image") continue;
                    if (type == "submit")
                    {
                        // 最初の送信ボタンだけ送る（PHP側で押されたボタンを確認していることがあるため）
                        if (!submitAdded) { list.Add(new(name, Attr(n, "value"))); submitAdded = true; }
                        continue;
                    }
                    if (type is "checkbox" or "radio" && !n.Attributes.Contains("checked")) continue;
                    list.Add(new(name, Attr(n, "value", type == "checkbox" ? "on" : "")));
                    break;

                case "select":
                    var options = n.SelectNodes(".//option");
                    var selected = n.SelectNodes(".//option[@selected]")?.ToList()
                                   ?? (options != null && !n.Attributes.Contains("multiple")
                                       ? new List<HtmlNode> { options[0] }
                                       : new List<HtmlNode>());
                    foreach (var opt in selected)
                        list.Add(new(name, Attr(opt, "value", Text(opt))));
                    break;

                case "textarea":
                    list.Add(new(name, HtmlEntity.DeEntitize(n.InnerText)));
                    break;

                case "button":
                    if (n.GetAttributeValue("type", "submit").ToLowerInvariant() == "submit" && !submitAdded)
                    {
                        list.Add(new(name, Attr(n, "value")));
                        submitAdded = true;
                    }
                    break;
            }
        }
        return list;
    }

    private Task<string> SubmitFormAsync(HtmlNode form, string pageUrl, List<KeyValuePair<string, string>> fields)
    {
        var action = HtmlEntity.DeEntitize(form.GetAttributeValue("action", ""));
        var actionUrl = string.IsNullOrEmpty(action) ? pageUrl : new Uri(new Uri(pageUrl), action).ToString();

        if (form.GetAttributeValue("method", "get").Equals("post", StringComparison.OrdinalIgnoreCase))
            return PostFormAsync(actionUrl, fields);

        var sep = actionUrl.Contains('?') ? "&" : "?";
        return GetAsync(actionUrl + sep + EncodeForm(fields));
    }

    private static void SetField(List<KeyValuePair<string, string>> fields, string name, string value)
    {
        fields.RemoveAll(f => f.Key == name);
        fields.Add(new(name, value));
    }
}
