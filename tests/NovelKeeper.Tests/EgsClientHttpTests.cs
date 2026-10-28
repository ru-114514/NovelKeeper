using System.Net;
using System.Text;
using System.Web;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>批評空間との通信を偽の応答に差し替えて、ページの作りに依存する部分（表の読み取り・フォームの送信）を確かめる</summary>
public class EgsClientHttpTests
{
    /// <summary>送られたリクエストを記録して、決めておいた応答を返す</summary>
    private sealed class FakeSite(Func<HttpRequestMessage, string, string> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            var html = respond(request, body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            };
        }
    }

    private static (EgsClient Client, FakeSite Site) Create(Func<HttpRequestMessage, string, string> respond)
    {
        var site = new FakeSite(respond);
        var cfg = new EgsConfig { EffectiveIntervalMs = 0 };
        return (new EgsClient(cfg, () => site), site);
    }

    private static string Result(string table) =>
        $"""<div id="query_result_header"><h3>SQLの実行結果</h3></div><div id="query_result_main">{table}</div>""";

    // ---- 読み込み（SQLページの表 → 作品・記録） ----

    [Fact]
    public async Task GetMyGames_MapsColumns_AndSkipsRowsWithoutId()
    {
        var (client, _) = Create((_, _) => Result("""
            <table>
            <tr><th>id</th><th>gamename</th><th>brandname</th><th>sellday</th><th>median</th><th>average2</th><th>count2</th><th>played</th><th>cleardate</th><th>possession</th><th>giveup</th><th>score</th></tr>
            <tr><td>101</td><td>作品A</td><td>ブランドX</td><td>2020-01-31</td><td>80</td><td>78.6</td><td>250</td><td>t</td><td>2021-05-06 00:00:00</td><td>t</td><td>f</td><td>85</td></tr>
            <tr><td>102</td><td>作品B</td><td>ブランドY</td><td>2022-12-01</td><td></td><td></td><td></td><td>f</td><td></td><td>f</td><td>t</td><td></td></tr>
            <tr><td></td><td>IDなし</td><td>Z</td><td></td><td></td><td></td><td></td><td>t</td><td></td><td>t</td><td>f</td><td></td></tr>
            </table>
            """));

        var games = await client.GetMyGamesAsync("me", ImportScope.All);

        Assert.Equal(2, games.Count); // id が読めない行は捨てる
        var a = games[0];
        Assert.Equal(101, a.Game.Id);
        Assert.Equal("作品A", a.Game.Title);
        Assert.Equal("ブランドX", a.Game.Brand);
        Assert.Equal(80, a.Game.Median);
        Assert.Equal(79, a.Game.Average); // 小数は丸める
        Assert.Equal(250, a.Game.Count);
        Assert.True(a.Played);
        Assert.Equal(new DateTime(2021, 5, 6), a.ClearDate);
        Assert.True(a.Possession);
        Assert.False(a.GiveUp);
        Assert.Equal(85, a.Score);

        var b = games[1];
        Assert.Null(b.Game.Median);
        Assert.Null(b.ClearDate);
        Assert.Null(b.Score);
        Assert.True(b.GiveUp);
        Assert.False(b.Played);
    }

    [Fact]
    public async Task Search_EscapesSingleQuotes_InTheSentSql()
    {
        var (client, site) = Create((_, _) => Result(""));

        await client.SearchAsync("a'b");

        var sent = HttpUtility.UrlDecode(site.Requests.Single().Body);
        Assert.Contains("ILIKE '%a''b%'", sent);
    }

    [Fact]
    public async Task MaintenancePage_ThrowsInsteadOfBeingTreatedAsZeroRecords()
    {
        var (client, _) = Create((_, _) => "<html><body>ただいまメンテナンス中です</body></html>");

        await Assert.ThrowsAsync<EgsException>(() => client.GetUserRecordsAsync("me"));
    }

    [Fact]
    public async Task GetReviews_DecodesBase64Text_AndKeepsMultilineMemo()
    {
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        var (client, _) = Create((_, _) => Result($"""
            <table>
            <tr><th>game</th><th>possession</th><th>total_play_time</th><th>okazu_tokuten</th><th>hitokoto_b64</th><th>memo_b64</th><th>pmemo_b64</th><th>platform</th></tr>
            <tr><td>7</td><td>t</td><td>12</td><td>-999</td><td>{B64("一言")}</td><td>{B64("1行目\r\n2行目 <b>&</b>")}</td><td></td><td>3</td></tr>
            </table>
            """));

        var reviews = await client.GetReviewsAsync("me");

        var r = reviews[7];
        Assert.True(r.Possession);
        Assert.Equal(12, r.TotalPlayTime);
        Assert.Null(r.Okazu); // -999 は「未入力」
        Assert.Equal("一言", r.Hitokoto);
        Assert.Equal("1行目\r\n2行目 <b>&</b>", r.Memo);
        Assert.Equal(3, r.Platform);
        Assert.Empty(r.Unreadable);
    }

    [Fact]
    public async Task GetReviews_BrokenBase64_IsMarkedUnreadable_NotBlanked()
    {
        var (client, _) = Create((_, _) => Result("""
            <table>
            <tr><th>game</th><th>hitokoto_b64</th><th>memo_b64</th><th>pmemo_b64</th></tr>
            <tr><td>7</td><td>!!壊れた!!</td><td></td><td></td></tr>
            </table>
            """));

        var r = (await client.GetReviewsAsync("me"))[7];

        Assert.Contains("hitokoto", r.Unreadable); // 空欄として取り込んで、手元の感想を消さない
    }

    // ---- ログイン（パスワードの送り先） ----

    [Theory]
    [InlineData("http://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/login_check.php", "post")]  // 暗号化されない通信
    [InlineData("https://evil.example.com/steal.php", "post")]                                      // 別のサイト
    [InlineData("https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/login_check.php", "get")]  // URL にパスワードが載る送り方
    public async Task Login_RefusesToSendPassword_ToUnexpectedDestination(string action, string method)
    {
        var (client, site) = Create((_, _) => $"""
            <form action="{action}" method="{method}">
              <input type="text" name="user"><input type="password" name="pass">
            </form>
            """);

        var ex = await Assert.ThrowsAsync<EgsException>(() => client.LoginAsync("u", "secret-pass"));

        Assert.Contains("パスワードを送信しませんでした", ex.Message);
        Assert.Single(site.Requests); // ログインページの取得だけ。フォームは送っていない
        Assert.DoesNotContain(site.Requests, r => r.Body.Contains("secret-pass") || r.Url.Contains("secret-pass"));
    }

    // ---- 書き込み（得点登録フォーム） ----

    private const string ReviewFormHtml = """
        <form action="contents_post.php" method="post">
          <input type="hidden" name="csrf" value="tok123">
          <input type="hidden" name="data[play]" value="0">
          <input type="checkbox" name="data[play]" value="1">
          <input type="text" name="data[finish_date]" value="">
          <input type="text" name="data[tokuten]" value="70">
          <textarea name="data[memo]">元の感想&#10;二行目</textarea>
          <input type="submit" name="submit" value="登録">
        </form>
        """;

    private static Dictionary<string, List<string>> ParseBody(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => HttpUtility.UrlDecode(p[0]), p => HttpUtility.UrlDecode(p.Length > 1 ? p[1] : ""))
            .ToDictionary(g => g.Key, g => g.ToList());

    [Fact]
    public async Task UpdateReview_SendsChangedFields_AndKeepsTheRestOfTheForm()
    {
        var (client, site) = Create((_, _) => ReviewFormHtml);
        client.LoggedIn = true; // ログインは別のテストで確認済み。ここでは保存済みの資格情報に触れない

        await client.UpdateReviewAsync(5, played: true, clearDate: new DateTime(2026, 3, 9), score: 88);

        var post = site.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.EndsWith("contents_post.php", post.Url);
        var f = ParseBody(post.Body);
        Assert.Equal(["1"], f["data[play]"]);              // ☑を付けた
        Assert.Equal(["2026-03-09"], f["data[finish_date]"]);
        Assert.Equal(["88"], f["data[tokuten]"]);
        Assert.Equal(["tok123"], f["csrf"]);               // 隠し項目はそのまま返す
        Assert.Equal(["元の感想\n二行目"], f["data[memo]"]);  // 触っていない感想は今の値のまま
        Assert.Equal(["登録"], f["submit"]);
    }

    [Fact]
    public async Task UpdateReview_Unplay_SendsHiddenZero_AndBlanksDate_ButNotScoreUnlessAsked()
    {
        var form = ReviewFormHtml.Replace("name=\"data[play]\" value=\"1\">", "name=\"data[play]\" value=\"1\" checked>")
                                 .Replace("name=\"data[finish_date]\" value=\"\"", "name=\"data[finish_date]\" value=\"2026-01-02\"");
        var (client, site) = Create((_, _) => form);
        client.LoggedIn = true;

        await client.UpdateReviewAsync(5, played: false, clearDate: null, score: null);

        var f = ParseBody(site.Requests.Single(r => r.Method == HttpMethod.Post).Body);
        Assert.Equal(["0"], f["data[play]"]);              // 隠し項目の「0」だけ（チェック欄は送らない）
        Assert.Equal([""], f["data[finish_date]"]);        // 残った日付を再送信しない
        Assert.Equal(["70"], f["data[tokuten]"]);          // 得点は clearScore を指定しない限り今のまま
    }

    // 発売前の作品のページ：プレイ済・得点の欄がなく、購入予定と購入前得点だけのフォーム
    private const string PreReleaseFormHtml = """
        <form action="contents_post.php" method="post">
          <input type="hidden" name="csrf" value="tok123">
          <select name="data[before_purchase_will]"><option value="">-</option><option value="1" selected>必ず購入</option></select>
          <input type="text" name="data[before_tokuten]" value="">
          <input type="submit" name="submit" value="登録">
        </form>
        """;

    [Fact]
    public async Task UpdateReview_PreReleaseForm_SendsOnlyFieldsThatExist_AndLeavesPlayStateAlone()
    {
        var (client, site) = Create((_, _) => PreReleaseFormHtml);
        client.LoggedIn = true;

        await client.UpdateReviewAsync(5, played: false, clearDate: null, score: null, clearScore: true,
            extra: new Dictionary<string, string?>
            {
                ["data[before_purchase_will]"] = "",
                ["data[hitokoto]"] = "x", // 発売前のフォームにない欄は送らない
            });

        var f = ParseBody(site.Requests.Single(r => r.Method == HttpMethod.Post).Body);
        Assert.Equal([""], f["data[before_purchase_will]"]);
        Assert.Equal(["tok123"], f["csrf"]);
        Assert.Equal(["登録"], f["submit"]);
        Assert.DoesNotContain("data[hitokoto]", f.Keys);
        Assert.DoesNotContain("data[play]", f.Keys);
        Assert.DoesNotContain("data[tokuten]", f.Keys);
        Assert.DoesNotContain("data[finish_date]", f.Keys);
    }

    [Fact]
    public async Task UpdateReview_NoKnownForm_StillThrows()
    {
        var (client, _) = Create((_, _) => "<form action=\"x.php\" method=\"post\"><input type=\"text\" name=\"other\"></form>");
        client.LoggedIn = true;

        await Assert.ThrowsAsync<EgsException>(() => client.UpdateReviewAsync(5, played: true, clearDate: null, score: null));
    }

    [Fact]
    public async Task UpdateReview_ClearScore_BlanksTheScoreField()
    {
        var (client, site) = Create((_, _) => ReviewFormHtml);
        client.LoggedIn = true;

        await client.UpdateReviewAsync(5, played: false, clearDate: null, score: null, clearScore: true);

        var f = ParseBody(site.Requests.Single(r => r.Method == HttpMethod.Post).Body);
        Assert.Equal([""], f["data[tokuten]"]);
    }
}
