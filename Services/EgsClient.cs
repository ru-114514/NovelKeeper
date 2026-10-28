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

public class EgsException(string message) : Exception(message);

/// <summary>批評空間の作品（検索結果）</summary>
public record EgsGame(int Id, string Title, string Brand, string SellDay, int? Median, int? Average = null, int? Count = null);

/// <summary>作品の最新の情報（中央値・平均値・データ数・発売日）</summary>
public record EgsGameInfo(int Id, string SellDay, int? Median, int? Average, int? Count);

/// <summary>批評空間に登録されている自分の記録</summary>
public record EgsUserRecord(int GameId, bool Played, DateTime? ClearDate, bool GiveUp, int? Score, bool Possession, DateTime? Registered = null);

/// <summary>一括取り込み用：作品情報と自分の記録をまとめたもの</summary>
public record EgsMyGame(EgsGame Game, bool Played, DateTime? ClearDate, bool Possession, bool GiveUp, int? Score, DateTime? Registered = null);

/// <summary>一括取り込みの範囲</summary>
public enum ImportScope
{
    PlayedOnly,          // プレイ済だけ
    PlayedAndPossession, // プレイ済＋所持
    All,                 // 批評空間に登録しているすべて
}

/// <summary>
/// 批評空間との通信。
///  - 読み込み：SQLページに SELECT 文を送り、結果の表を解析する
///  - 書き込み：ログインして得点登録フォームを読み込み、必要な項目だけ書き換えて送信する
/// </summary>
public class EgsClient
{
    private readonly EgsConfig _cfg;
    private HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    /// <summary>テストで通信を差し替えるための工場（null なら通常の通信）</summary>
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    public bool LoggedIn { get; internal set; }

    static EgsClient()
    {
        // HtmlAgilityPack は初期設定だと <form> の中身を子要素として扱わないので、その設定を外す
        HtmlNode.ElementsFlags.Remove("form");
        // EUC-JP / Shift_JIS を使えるようにする
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EgsClient(EgsConfig cfg) : this(cfg, null) { }

    internal EgsClient(EgsConfig cfg, Func<HttpMessageHandler>? handlerFactory)
    {
        _cfg = cfg;
        _handlerFactory = handlerFactory;
        _http = CreateHttp(handlerFactory);
    }

    private static HttpClient CreateHttp(Func<HttpMessageHandler>? handlerFactory)
    {
        HttpMessageHandler handler = handlerFactory?.Invoke() ?? new HttpClientHandler
        {
            CookieContainer = new CookieContainer(), // ログイン状態（Cookie）を保持
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NovelKeeper/1.0 (personal use)");
        return http;
    }

    /// <summary>ログイン状態（Cookie）を捨てる。ログイン情報の削除・別のIDへの切り替え時に使う</summary>
    public void Logout()
    {
        LoggedIn = false;
        _http = CreateHttp(_handlerFactory); // 古い方は、通信中のものがあっても壊さないよう Dispose せず、GC に任せる
    }

    /// <summary>ログインの調査用ファイルを消す</summary>
    public static void DeleteDebugFiles()
    {
        foreach (var name in new[] { "debug_login_form.txt", "debug_login_result.html", "debug_login_top.html" })
        {
            try
            {
                var path = Path.Combine(AppPaths.Dir, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* 消せなくても動作には影響しない */ }
        }
    }

    private Encoding PageEncoding => Encoding.GetEncoding(_cfg.CharSet);

    // ================================================================
    // 読み込み（SQLページ）
    // ================================================================

    /// <summary>タイトルで作品を検索する</summary>
    public async Task<List<EgsGame>> SearchAsync(string keyword)
    {
        return (await QueryAsync(EgsSearchQuery.Build(keyword))).Select(ToGame).Where(g => g.Id > 0).ToList();
    }

    /// <summary>検索結果の1行を作品にする（id, gamename, brandname, sellday, median, average2, count2 の列が必要）</summary>
    private static EgsGame ToGame(Dictionary<string, string> r) => new(
        int.TryParse(r.GetValueOrDefault("id", ""), out var id) ? id : 0,
        r.GetValueOrDefault("gamename", ""),
        r.GetValueOrDefault("brandname", ""),
        r.GetValueOrDefault("sellday", ""),
        ParseNum(r.GetValueOrDefault("median", "")),
        ParseNum(r.GetValueOrDefault("average2", "")),
        ParseNum(r.GetValueOrDefault("count2", "")));

    /// <summary>作品の最新の情報（中央値・平均値・データ数・発売日）をまとめて取得する</summary>
    public async Task<List<EgsGameInfo>> GetGameInfosAsync(IEnumerable<int> ids)
    {
        var result = new List<EgsGameInfo>();
        foreach (var chunk in ids.Distinct().Chunk(400)) // 1回の問い合わせが長くなりすぎないよう分ける
        {
            var sql = $"""
                SELECT id, sellday, median, average2, count2
                FROM gamelist
                WHERE id IN ({string.Join(",", chunk)})
                """;
            var rows = await QueryAsync(sql);
            result.AddRange(rows.Select(r => new EgsGameInfo(
                int.TryParse(r.GetValueOrDefault("id", ""), out var id) ? id : 0,
                r.GetValueOrDefault("sellday", ""),
                ParseNum(r.GetValueOrDefault("median", "")),
                ParseNum(r.GetValueOrDefault("average2", "")),
                ParseNum(r.GetValueOrDefault("count2", "")))).Where(x => x.Id > 0));
        }
        return result;
    }

    /// <summary>POV を「特徴」とみなす最小の人数</summary>
    internal const int TraitMinVotes = 2;

    /// <summary>
    /// 作品の特徴（ジャンル・総プレイ時間の中央値・POV）をまとめて取得する。
    /// POV はランクAが一定数以上のものだけを残す
    /// </summary>
    public async Task<List<GameTraits>> GetTraitsAsync(IEnumerable<int> ids, Action<int, int>? progress = null)
    {
        var all = ids.Distinct().ToList();
        var result = new List<GameTraits>();
        var done = 0;
        foreach (var chunk in all.Chunk(60)) // 批評空間のSQLは10秒で打ち切られるので、小さく分ける
        {
            var list = string.Join(",", chunk);
            var traits = chunk.ToDictionary(id => id, id => new GameTraits { EgsId = id, FetchedAt = DateTime.Now });

            foreach (var r in await QueryAsync($"SELECT id, genre, total_play_time_median FROM gamelist WHERE id IN ({list})"))
            {
                if (!int.TryParse(r.GetValueOrDefault("id", ""), out var id) || !traits.TryGetValue(id, out var t)) continue;
                t.Genre = r.GetValueOrDefault("genre", "").Trim();
                t.PlayHours = ParseNum(r.GetValueOrDefault("total_play_time_median", ""));
            }
            // 「ネガティブ」のPOVは、シナリオに関わる3つ以外は取得しない
            var kept = string.Join(",", TraitMatcher.KeptNegativePovIds);
            foreach (var r in await QueryAsync(
                         $"SELECT t.game AS game, l.title AS title FROM povgroups_toukei t JOIN povlist l ON l.id = t.pov " +
                         $"WHERE t.game IN ({list}) AND t.rank = 'A' AND t.count >= {TraitMinVotes} " +
                         $"AND (l.system_group <> 'ネガティブ' OR l.id IN ({kept}))"))
            {
                if (int.TryParse(r.GetValueOrDefault("game", ""), out var id) && traits.TryGetValue(id, out var t)
                    && r.GetValueOrDefault("title", "").Trim() is { Length: > 0 } name && !t.Pov.Contains(name))
                    t.Pov.Add(name);
            }
            result.AddRange(traits.Values);
            done += chunk.Length;
            progress?.Invoke(done, all.Count);
        }
        return result;
    }

    /// <summary>自分（uid）の userreview から、プレイ済とクリア日を取得する</summary>
    public async Task<List<EgsUserRecord>> GetUserRecordsAsync(string uid)
    {
        var clearExpr = ClearDateExpr("");
        var sql = $"""
            SELECT game, {_cfg.PlayedColumn} AS played, {clearExpr} AS cleardate, giveup AS giveup, {ScoreExpr("")} AS score, possession AS possession, userreview.timestamp AS registered
            FROM userreview
            WHERE uid = '{Esc(uid)}'
            """;
        var rows = await QueryAsync(sql);
        return rows.Select(r => new EgsUserRecord(
                int.TryParse(r.GetValueOrDefault("game", ""), out var id) ? id : 0,
                IsTrue(r.GetValueOrDefault("played", "")),
                ParseDate(r.GetValueOrDefault("cleardate", "")),
                IsTrue(r.GetValueOrDefault("giveup", "")),
                ParseInt(r.GetValueOrDefault("score", "")),
                IsTrue(r.GetValueOrDefault("possession", "")),
                ParseDate(r.GetValueOrDefault("registered", ""))))
            .Where(r => r.GameId > 0)
            .ToList();
    }

    /// <summary>
    /// 自分（uid）が批評空間に登録している作品を、範囲（scope）を指定して作品情報付きでまとめて取得する
    /// </summary>
    public async Task<List<EgsMyGame>> GetMyGamesAsync(string uid, ImportScope scope)
    {
        var clearExpr = ClearDateExpr("u.");
        var condition = scope switch
        {
            ImportScope.PlayedOnly => $"u.{_cfg.PlayedColumn} = true",
            ImportScope.PlayedAndPossession => $"u.{_cfg.PlayedColumn} = true OR u.possession = true",
            _ => "true", // 登録しているすべて
        };
        var sql = $"""
            SELECT g.id, g.gamename, b.brandname, g.sellday, g.median, g.average2, g.count2,
                   u.{_cfg.PlayedColumn} AS played, {clearExpr} AS cleardate, u.possession AS possession, u.giveup AS giveup, {ScoreExpr("u.")} AS score, u.timestamp AS registered
            FROM userreview u
            JOIN gamelist g ON u.game = g.id
            JOIN brandlist b ON g.brandname = b.id
            WHERE u.uid = '{Esc(uid)}' AND ({condition})
            ORDER BY g.sellday
            """;
        var rows = await QueryAsync(sql);
        return rows.Select(r => new EgsMyGame(
                ToGame(r),
                IsTrue(r.GetValueOrDefault("played", "")),
                ParseDate(r.GetValueOrDefault("cleardate", "")),
                IsTrue(r.GetValueOrDefault("possession", "")),
                IsTrue(r.GetValueOrDefault("giveup", "")),
                ParseInt(r.GetValueOrDefault("score", "")),
                ParseDate(r.GetValueOrDefault("registered", ""))))
            .Where(x => x.Game.Id > 0)
            .ToList();
    }

    /// <summary>
    /// 自分（uid）の得点登録フォームのその他の項目（所持・総プレイ時間・感想・platform など）を、作品ごとにまとめて取得する。
    /// 感想などの文章は、改行や記号が崩れないよう base64 にして受け取る（使えないときは、そのまま受け取る）。
    /// </summary>
    public async Task<Dictionary<int, Review>> GetReviewsAsync(string uid, int? gameId = null)
    {
        var gameFilter = gameId is int gid ? $" AND u.game = {gid}" : "";
        string Sql(bool b64)
        {
            string T(string expr, string alias) => b64
                ? $"encode(convert_to(COALESCE({expr}, ''), 'UTF8'), 'base64') AS {alias}_b64"
                : $"{expr} AS {alias}";
            return $"""
                SELECT u.game, u.possession, u.total_play_time, u.time_before_understanding_fun, u.okazu_tokuten,
                       u.before_purchase_will, u.before_tokuten, u.start_date, u.sage, u.netabare,
                       {T("u.hitokoto", "hitokoto")}, {T("u.memo", "memo")},
                       p.platform, p.operation, {T("p.memo", "pmemo")}
                FROM userreview u
                LEFT JOIN support_platform p ON p.uid = u.uid AND p.game = u.game
                WHERE u.uid = '{Esc(uid)}'{gameFilter}
                """;
        }

        List<Dictionary<string, string>> rows;
        try
        {
            rows = await QueryAsync(Sql(true));
            if (rows.Count == 0 || !rows[0].ContainsKey("memo_b64")) throw new EgsException("base64 が使えません");
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            throw; // 時間切れのときは、重い問い合わせを重ねて送らない
        }
        catch (Exception ex)
        {
            Log.Warn("感想の base64 取得に失敗したため、そのまま取得します", ex);
            rows = await QueryAsync(Sql(false)); // base64 が使えないときは、文章をそのまま受け取る
        }

        // 読み取れなかった文章は null（その項目は取り込まない。空欄で上書きしないため）
        string? TextOf(Dictionary<string, string> r, string alias)
        {
            if (r.TryGetValue(alias + "_b64", out var b))
            {
                var clean = Regex.Replace(b, @"\s", "");
                if (clean == "") return "";
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(clean)); }
                catch { return null; }
            }
            return r.TryGetValue(alias, out var plain) ? plain : null;
        }

        var dict = new Dictionary<int, Review>();
        foreach (var r in rows)
        {
            if (!int.TryParse(r.GetValueOrDefault("game", ""), out var id) || id <= 0 || dict.ContainsKey(id)) continue;
            var okazu = ParseInt(r.GetValueOrDefault("okazu_tokuten", ""));
            var hitokoto = TextOf(r, "hitokoto");
            var memo = TextOf(r, "memo");
            var pmemo = TextOf(r, "pmemo");
            var review = new Review
            {
                Possession = IsTrue(r.GetValueOrDefault("possession", "")),
                TotalPlayTime = ParseInt(r.GetValueOrDefault("total_play_time", "")),
                FunTime = ParseInt(r.GetValueOrDefault("time_before_understanding_fun", "")),
                Okazu = okazu is null or -999 ? null : okazu,
                PurchaseWill = r.GetValueOrDefault("before_purchase_will", "").Trim(),
                BeforeScore = ParseInt(r.GetValueOrDefault("before_tokuten", "")),
                StartDate = ParseDate(r.GetValueOrDefault("start_date", "")),
                Sage = IsTrue(r.GetValueOrDefault("sage", "")),
                Netabare = IsTrue(r.GetValueOrDefault("netabare", "")),
                Hitokoto = hitokoto ?? "",
                Memo = memo ?? "",
                Platform = ParseInt(r.GetValueOrDefault("platform", "")),
                Operation = ParseInt(r.GetValueOrDefault("operation", "")),
                PlatformMemo = pmemo ?? "",
            };
            if (hitokoto == null) review.Unreadable.Add("hitokoto");
            if (memo == null) review.Unreadable.Add("memo");
            if (pmemo == null) review.Unreadable.Add("platform");
            dict[id] = review;
        }
        return dict;
    }

    /// <summary>問い合わせが時間切れになったか（通信の時間切れ、またはサイト側のSQLの打ち切り）</summary>
    internal static bool IsTimeout(Exception ex) => ex switch
    {
        TimeoutException => true,
        TaskCanceledException => true,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.GatewayTimeout } => true,
        EgsException e => e.Message.Contains("statement timeout", StringComparison.OrdinalIgnoreCase) ||
                          e.Message.Contains("canceling statement", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>作品の画像の候補（DMM・駿河屋・バナー）を、批評空間の作品情報から作る</summary>
    public async Task<List<string>> GetImageCandidatesAsync(int egsId)
    {
        var rows = await QueryAsync($"SELECT dmm, dmm_genre, dmm_genre_2, surugaya_1, banner_url FROM gamelist WHERE id = {egsId}");
        var list = new List<string>();
        var r = rows.FirstOrDefault();
        if (r == null) return list;

        var dmm = r.GetValueOrDefault("dmm", "").Trim();
        var genre = r.GetValueOrDefault("dmm_genre", "").Trim();
        var genre2 = r.GetValueOrDefault("dmm_genre_2", "").Trim();
        if (dmm != "")
        {
            if (genre != "" && genre2 != "") list.Add($"https://pics.dmm.co.jp/{genre}/{genre2}/{dmm}/{dmm}pl.jpg");
            list.Add($"https://pics.dmm.co.jp/digital/pcgame/{dmm}/{dmm}pl.jpg");
            list.Add($"https://pics.dmm.co.jp/mono/game/{dmm}/{dmm}pl.jpg");
        }
        var suruga = r.GetValueOrDefault("surugaya_1", "").Trim();
        if (suruga != "") list.Add($"https://www.suruga-ya.jp/database/pics_light/game/{suruga}.jpg");
        var banner = r.GetValueOrDefault("banner_url", "").Trim();
        if (banner.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) list.Add(banner); // 取得先は GameImageService で限定する
        return list.Distinct().ToList();
    }

    /// <summary>SQLページに SELECT 文を送り、結果の表を「列名→値」の辞書のリストにする</summary>
    public async Task<List<Dictionary<string, string>>> QueryAsync(string sql)
    {
        var html = await PostFormAsync(_cfg.Url(_cfg.SqlPage),
            new List<KeyValuePair<string, string>> { new(_cfg.SqlFieldName, sql) });
        return ParseResultTable(html);
    }

    private const string SqlResultMarker = "SQLの実行結果";
    private const string SqlErrorMarker = "エラー(SQL間違い)";

    /// <summary>
    /// SQLページの応答を表にする。
    /// 結果が0件のときは表が出ないので、「SQLの実行結果」の見出しがあるかで、本当に0件なのか、
    /// 別のページ（メンテナンス中・エラー）が返ってきたのかを見分ける。後者は例外にする
    /// （「記録なし」と取り違えて、同期で全作品を未登録扱いにしないため）。
    /// </summary>
    internal static List<Dictionary<string, string>> ParseResultTable(string html)
    {
        if (!html.Contains(SqlResultMarker))
        {
            if (html.Contains(SqlErrorMarker))
            {
                var detail = Regex.Match(html, @"ERROR:[^<]*").Value;
                throw new EgsException("批評空間のSQLがエラーになりました。" + HtmlEntity.DeEntitize(detail));
            }
            throw new EgsException("批評空間から想定外の応答が返りました（メンテナンス中、またはページの作りが変わった可能性があります）。");
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        // 見出し(th)を持つ表のうち、最後のものを結果の表とみなす
        var table = doc.DocumentNode.SelectNodes("//table[.//th]")?.LastOrDefault();
        var result = new List<Dictionary<string, string>>();
        if (table == null) return result;

        var headers = table.SelectNodes(".//th")!
            .Select(th => Text(th))
            .ToList();

        foreach (var tr in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var tds = tr.SelectNodes("./td");
            if (tds == null) continue;
            var row = new Dictionary<string, string>();
            for (var i = 0; i < Math.Min(headers.Count, tds.Count); i++)
                row[headers[i]] = Text(tds[i]);
            result.Add(row);
        }
        return result;
    }

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
        var action = HtmlEntity.DeEntitize(form.GetAttributeValue("action", ""));
        var actionUri = string.IsNullOrEmpty(action) ? new Uri(url) : new Uri(new Uri(url), action);
        var baseUri = new Uri(_cfg.BaseUrl);
        if (actionUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(actionUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
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

    // ================================================================
    // HTTP
    // ================================================================

    private Task<string> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

    private Task<string> PostFormAsync(string url, List<KeyValuePair<string, string>> fields)
    {
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(EncodeForm(fields)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = content });
    }

    private string EncodeForm(List<KeyValuePair<string, string>> fields) =>
        string.Join("&", fields.Select(f =>
            HttpUtility.UrlEncode(f.Key, PageEncoding) + "=" + HttpUtility.UrlEncode(f.Value, PageEncoding)));

    /// <summary>1件ずつ、間隔をあけて送信する</summary>
    private async Task<string> SendAsync(HttpRequestMessage request)
    {
        await _lock.WaitAsync();
        try
        {
            var wait = _cfg.EffectiveIntervalMs - (int)(DateTime.Now - _lastRequest).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait);

            using var res = await _http.SendAsync(request);
            _lastRequest = DateTime.Now;
            res.EnsureSuccessStatusCode();

            var bytes = await res.Content.ReadAsByteArrayAsync();
            var enc = PageEncoding;
            var charset = res.Content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrEmpty(charset))
            {
                try { enc = Encoding.GetEncoding(charset.Trim('"')); } catch { /* 設定の文字コードを使う */ }
            }
            return enc.GetString(bytes);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================================================
    // 小さな補助関数
    // ================================================================

    private static HtmlDocument Load(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    private static string Text(HtmlNode n) => HtmlEntity.DeEntitize(n.InnerText).Trim();

    private static string Attr(HtmlNode n, string name, string def = "") =>
        HtmlEntity.DeEntitize(n.GetAttributeValue(name, def));

    /// <summary>SQL の文字列リテラル用にシングルクォートをエスケープ</summary>
    private static string Esc(string s) => s.Replace("'", "''");

    private static void CheckColumnName(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new EgsException($"列名が正しくありません: {name}");
    }

    /// <summary>「プレイ済」の列名を検査し、クリア日の列（未設定なら NULL）を返す</summary>
    private string ClearDateExpr(string prefix)
    {
        if (!EgsConfig.IsSet(_cfg.PlayedColumn))
            throw new EgsException("egs_config.json の PlayedColumn が未設定です。");
        CheckColumnName(_cfg.PlayedColumn);
        if (!EgsConfig.IsSet(_cfg.ClearDateColumn)) return "NULL";
        CheckColumnName(_cfg.ClearDateColumn);
        return prefix + _cfg.ClearDateColumn;
    }

    /// <summary>得点の列（未設定なら NULL）</summary>
    private string ScoreExpr(string prefix)
    {
        if (!EgsConfig.IsSet(_cfg.ScoreColumn)) return "NULL";
        CheckColumnName(_cfg.ScoreColumn);
        return prefix + _cfg.ScoreColumn;
    }

    private static int? ParseInt(string s) => int.TryParse(s.Trim(), out var v) ? v : null;

    /// <summary>整数・小数どちらでも読み、整数に丸める</summary>
    private static int? ParseNum(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)Math.Round(v) : null;

    private static bool IsTrue(string s) =>
        s.Trim().ToLowerInvariant() is "t" or "true" or "1" or "on" or "yes";

    private static DateTime? ParseDate(string s)
    {
        s = s.Trim();
        if (s.Length >= 10 && DateTime.TryParse(s[..10], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.Date;
        return null;
    }
}
