namespace NovelKeeper.Services;

/// <summary>投稿設定のパネルの種類</summary>
public enum PostPanelKind
{
    TotalTime = 0,  // 総プレイ時間
    ClearCount = 1, // クリア数
    Score = 2,      // 点数
    Backlog = 3,    // 積みゲーの残り本数
    BestBrand = 4,  // マイベストブランド
    Works = 5,      // プレイした作品
    Priority = 6,   // 今後優先したい作品（選んだ未プレイの作品）
    DaysPlayed = 7,       // プレイした日数
    AvgPerDay = 8,        // 1日の平均プレイ時間
    WorksCount = 9,       // プレイした作品数
    LongestSession = 10,  // 最長の1回
    BestDay = 11,         // 最も遊んだ日
    PeakHour = 12,        // よく遊ぶ時間帯
    PrevComparison = 13,  // 前の期間との比較
    ScoreVsMedian = 14,   // 自分の点と中央値の差
    ClearedWorks = 15,    // クリアした作品の一覧
    TopScoreWorks = 16,   // 高得点の作品
    BrandRanking = 17,    // ブランド別ランキング
    DailyChart = 18,      // プレイ時間のグラフ
    ScoreDistribution = 19, // 点数の分布
    Calendar = 20,        // カレンダー（遊んだ日の色分け）
    Memo = 21,            // 自由に書ける文章
    Heading = 22,         // 見出し（区切り）
    Title = 23,           // 画像のタイトル（全カラムの幅。期間も表示できる）
    LongestStreak = 24,   // 最長の連続プレイ日数（1日10分以上）
    GoalProgress = 25,    // 目標の達成度（レポートで決めた年間・月間の目標）
}

/// <summary>投稿設定のパネル1枚分の設定</summary>
public class PostPanel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public PostPanelKind Kind { get; set; }
    /// <summary>パネルのタイトル（空なら、種類ごとの既定の名前）。「タイトル」パネルでは、画像の見出しの文字</summary>
    public string? Title { get; set; }
    /// <summary>置き場所：段（0 から）</summary>
    public int Row { get; set; }
    /// <summary>置き場所：列（0 から）</summary>
    public int Col { get; set; }
    /// <summary>横幅（カラム数）</summary>
    public int Span { get; set; } = 1;
    /// <summary>縦幅（段の数。1段の高さは決まっていて、パネルは常に段の整数倍の大きさになる）</summary>
    public int RowSpan { get; set; } = 1;
    /// <summary>以前の版の縦幅（ピクセル）。いまは使わない（古い設定を読むためだけに残している）</summary>
    public int Height { get; set; }
    /// <summary>作品を並べるパネルの表示件数</summary>
    public int ItemCount { get; set; } = 12;
    /// <summary>作品を並べるパネルの列数（0 なら自動：パネルの幅に入るだけ並べ、余った余白は表示位置に従って寄せる）</summary>
    public int ItemColumns { get; set; } = 3;
    /// <summary>カレンダー（1行に並べる月の数）・ブランド別ランキング（行を分ける列数）の列数。0 なら自動（カレンダーは幅に合わせて折り返し、ランキングは1列）</summary>
    public int ViewColumns { get; set; }
    /// <summary>中身の表示位置（余白ができるとき）：0=左寄せ、1=中央寄せ、2=右寄せ</summary>
    public int Align { get; set; }
    /// <summary>「今後優先したい作品」で選んだ作品（選んだ順）</summary>
    public List<long> GameIds { get; set; } = new();
    /// <summary>「メモ」の文章</summary>
    public string? Text { get; set; }
    /// <summary>文字の設定（タイトル・見出し・メモ）：フォント名（空なら標準）</summary>
    public string? FontFamily { get; set; }
    /// <summary>文字の設定：色（#RRGGBB。空なら標準）</summary>
    public string? FontColor { get; set; }
    /// <summary>文字の設定：大きさ（0 なら、種類ごとの標準）</summary>
    public int FontSize { get; set; }
    /// <summary>「タイトル」パネルを、画像の左右の端（1段目なら上の端も）まで広げるか（false は、ほかのパネルと同じ余白のある枠）</summary>
    public bool FullBleed { get; set; }
    /// <summary>このパネルだけの透過率（0〜100。null なら、デザイン設定の全体の透過率に従う）</summary>
    public int? Transparency { get; set; }
    /// <summary>「タイトル」パネルで、期間（2026/09/24 〜 2026/10/23）も表示するか</summary>
    public bool ShowPeriod { get; set; } = true;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? PostLayout.DefaultTitle(Kind) : Title.Trim();
}

/// <summary>
/// 投稿設定のレイアウト（全体のカラム数と、パネルの置き場所）。画面に触れないので、そのままテストできる。
///  - パネルは「段・列・横幅（カラム数）・縦幅（段数）」で置く。1段の高さは決まっていて、パネルは段の整数倍の大きさ
///  - 新しく追加したパネルは、空いている場所のうち、最も上の段の、最も左に置く
///  - 縦に複数の段をまたぐパネルの隣の、空いた段にも置ける
///  - 「タイトル」パネルは、いつも全カラムの幅
/// </summary>
public class PostLayout
{
    public const int MinColumns = 2, MaxColumns = 4;
    public const int MaxPriorityGames = 10;
    public const int MaxWorks = 30;
    public const int MaxRow = 100;
    public const int MaxRowSpan = 40;
    public const int MaxTextLength = 500;
    public const int CurrentVersion = 3;
    /// <summary>1段の高さ（px。パネルの外側の余白 4px ずつを含む）</summary>
    public const double RowUnitPx = 64;
    /// <summary>「タイトル」パネルの標準の縦幅（段）</summary>
    public const int TitleRowSpan = 2;

    /// <summary>設定の版（2 未満は、タイトルが上部の入力欄だった時代の設定。読み込み時に作り替える）</summary>
    public int Version { get; set; }
    public int Columns { get; set; } = 2;
    public List<PostPanel> Panels { get; set; } = new();

    public static string DefaultTitle(PostPanelKind kind) => kind switch
    {
        PostPanelKind.TotalTime => "総プレイ時間",
        PostPanelKind.ClearCount => "クリア数",
        PostPanelKind.Score => "点数",
        PostPanelKind.Backlog => "積みゲーの残り本数",
        PostPanelKind.BestBrand => "マイベストブランド",
        PostPanelKind.Works => "プレイした作品（プレイ時間順）",
        PostPanelKind.Priority => "今後優先したい作品",
        PostPanelKind.DaysPlayed => "プレイした日数",
        PostPanelKind.AvgPerDay => "1日の平均プレイ時間",
        PostPanelKind.WorksCount => "プレイした作品数",
        PostPanelKind.LongestSession => "最長の1回",
        PostPanelKind.BestDay => "最も遊んだ日",
        PostPanelKind.PeakHour => "よく遊ぶ時間帯",
        PostPanelKind.PrevComparison => "前の期間との比較",
        PostPanelKind.ScoreVsMedian => "自分の点と中央値の差",
        PostPanelKind.ClearedWorks => "クリアした作品",
        PostPanelKind.TopScoreWorks => "高得点の作品",
        PostPanelKind.BrandRanking => "ブランド別ランキング",
        PostPanelKind.DailyChart => "プレイ時間のグラフ",
        PostPanelKind.ScoreDistribution => "点数の分布",
        PostPanelKind.Calendar => "カレンダー",
        PostPanelKind.Memo => "メモ",
        PostPanelKind.Heading => "見出し",
        PostPanelKind.Title => "ノベルゲーム　プレイレポート",
        PostPanelKind.LongestStreak => "最長の連続プレイ日数",
        PostPanelKind.GoalProgress => "目標の達成度",
        _ => "",
    };

    /// <summary>種類の一覧（設定の選択欄に使う。値の順）</summary>
    public static readonly PostPanelKind[] AllKinds = Enum.GetValues<PostPanelKind>();

    /// <summary>追加メニューのグループ（名前と、そこに入れる種類）</summary>
    public static readonly (string Name, PostPanelKind[] Kinds)[] Groups =
    {
        ("数字", new[] { PostPanelKind.TotalTime, PostPanelKind.ClearCount, PostPanelKind.Score, PostPanelKind.DaysPlayed,
            PostPanelKind.AvgPerDay, PostPanelKind.WorksCount, PostPanelKind.LongestSession, PostPanelKind.BestDay,
            PostPanelKind.PeakHour, PostPanelKind.LongestStreak, PostPanelKind.GoalProgress, PostPanelKind.PrevComparison, PostPanelKind.ScoreVsMedian, PostPanelKind.BestBrand }),
        ("作品の一覧", new[] { PostPanelKind.Works, PostPanelKind.ClearedWorks, PostPanelKind.TopScoreWorks,
            PostPanelKind.BrandRanking, PostPanelKind.Priority }),
        ("グラフ", new[] { PostPanelKind.DailyChart, PostPanelKind.ScoreDistribution, PostPanelKind.Calendar }),
        ("積みゲー", new[] { PostPanelKind.Backlog }),
        ("文字", new[] { PostPanelKind.Title, PostPanelKind.Memo, PostPanelKind.Heading }),
    };

    /// <summary>作品や行を並べるパネルか（件数・列数の設定を持つ）</summary>
    public static bool UsesItems(PostPanelKind kind) => kind is PostPanelKind.Works or PostPanelKind.Priority
        or PostPanelKind.ClearedWorks or PostPanelKind.TopScoreWorks or PostPanelKind.BrandRanking;

    /// <summary>表示の列数（ViewColumns）を選べるパネルか（カレンダーの月・ランキングの行）</summary>
    public static bool UsesViewColumns(PostPanelKind kind) => kind is PostPanelKind.Calendar or PostPanelKind.BrandRanking;

    /// <summary>作品の並び（ItemColumns）の列数を持つパネルか。ランキングは ViewColumns を使う</summary>
    public static bool UsesItemColumns(PostPanelKind kind) => UsesItems(kind) && kind != PostPanelKind.BrandRanking;

    /// <summary>中身の表示位置（左・中央・右寄せ）を選べるパネルか</summary>
    public static bool UsesAlign(PostPanelKind kind) => kind is not (PostPanelKind.Title or PostPanelKind.Heading);

    /// <summary>文字の設定（フォント・色・大きさ）を持つパネルか</summary>
    public static bool UsesFont(PostPanelKind kind) => kind is PostPanelKind.Title or PostPanelKind.Heading or PostPanelKind.Memo;

    /// <summary>いつも全カラムの幅で、いちばん左に置くパネルか</summary>
    public static bool IsFullWidth(PostPanelKind kind) => kind == PostPanelKind.Title;

    /// <summary>移動できないパネルか（ドラッグも、設定での場所の変更もできない。ほかのパネルと入れ替えることもない）</summary>
    public static bool IsLocked(PostPanelKind kind) => kind == PostPanelKind.Title;

    /// <summary>表示件数の上限</summary>
    public static int MaxItems(PostPanelKind kind) => kind == PostPanelKind.Priority ? MaxPriorityGames : MaxWorks;

    /// <summary>
    /// 中身に必要な段数のおおよその見積り（1段 = 64px。作品の1組 = 約216px）。
    /// 画面で測れるときは、画面側で実際の大きさから求める。これは、初期値と古い設定の引き継ぎ用
    /// </summary>
    public static int EstimateRowSpan(PostPanelKind kind, int itemCount, int itemColumns)
    {
        double px = kind switch
        {
            PostPanelKind.Works or PostPanelKind.Priority or PostPanelKind.ClearedWorks or PostPanelKind.TopScoreWorks
                => 72 + Math.Ceiling(itemCount / (double)(itemColumns <= 0 ? 2 : itemColumns)) * 216,
            PostPanelKind.BrandRanking => 72 + itemCount * 26,
            PostPanelKind.DailyChart or PostPanelKind.ScoreDistribution => 240,
            PostPanelKind.Calendar => 330,
            _ => 0,
        };
        return px == 0 ? 2 : Math.Clamp((int)Math.Ceiling((px + 8) / RowUnitPx), 1, MaxRowSpan); // 数字などの小さなパネルは 2 段（約128px）
    }

    /// <summary>初期の配置：2カラム。1段目はタイトル（全カラムの幅）</summary>
    public static PostLayout CreateDefault()
    {
        var l = new PostLayout { Columns = 2, Version = CurrentVersion };
        (PostPanelKind, int, int)[] cells =
        {
            (PostPanelKind.Title, 0, 0),
            (PostPanelKind.TotalTime, 2, 0), (PostPanelKind.ClearCount, 2, 1),
            (PostPanelKind.Score, 4, 0), (PostPanelKind.Backlog, 4, 1),
            (PostPanelKind.BestBrand, 6, 0), (PostPanelKind.Works, 6, 1),
        };
        foreach (var (kind, row, col) in cells)
        {
            var p = NewPanel(kind);
            p.Row = row; p.Col = col;
            if (IsFullWidth(kind)) p.Span = l.Columns;
            l.Panels.Add(p);
        }
        return l;
    }

    public static PostPanel NewPanel(PostPanelKind kind)
    {
        var p = new PostPanel
        {
            Kind = kind,
            ItemCount = kind switch
            {
                PostPanelKind.Priority => 3,
                PostPanelKind.BrandRanking => 5,
                PostPanelKind.TopScoreWorks => 6,
                _ => 12,
            },
            ItemColumns = 3,
        };
        p.RowSpan = kind == PostPanelKind.Title ? TitleRowSpan : EstimateRowSpan(kind, p.ItemCount, p.ItemColumns);
        return p;
    }

    private static bool Overlaps(PostPanel o, int row, int col, int span, int rowSpan) =>
        o.Row < row + rowSpan && row < o.Row + o.RowSpan && o.Col < col + span && col < o.Col + o.Span;

    /// <summary>指定の場所が空いているか（ignore のパネルは数えない）</summary>
    public bool IsFree(int row, int col, int span, PostPanel? ignore = null, int rowSpan = 1) =>
        IsFree(Panels, row, col, span, rowSpan, ignore);

    private static bool IsFree(IEnumerable<PostPanel> others, int row, int col, int span, int rowSpan, PostPanel? ignore) =>
        !others.Any(o => !ReferenceEquals(o, ignore) && Overlaps(o, row, col, span, rowSpan));

    /// <summary>空いている場所のうち、最も上の段の、最も左を探す</summary>
    public (int Row, int Col) FindSlot(int span, int rowSpan = 1, PostPanel? ignore = null) =>
        FindSlot(Panels, Columns, span, rowSpan, ignore);

    private static (int Row, int Col) FindSlot(IReadOnlyCollection<PostPanel> others, int columns, int span, int rowSpan, PostPanel? ignore)
    {
        span = Math.Clamp(span, 1, columns);
        rowSpan = Math.Clamp(rowSpan, 1, MaxRowSpan);
        var lastRow = others.Where(o => !ReferenceEquals(o, ignore)).Select(o => o.Row + o.RowSpan - 1).DefaultIfEmpty(-1).Max();
        for (var row = 0; row <= lastRow + 1; row++)
            for (var col = 0; col + span <= columns; col++)
                if (IsFree(others, row, col, span, rowSpan, ignore)) return (row, col);
        return (lastRow + 1, 0); // 到達しない（最後の段の次は必ず空いている）
    }

    /// <summary>新しいパネルを、空いている場所のうち、最も上の段の、最も左に置いて返す（縦幅を省くと、中身の見積り）</summary>
    public PostPanel Add(PostPanelKind kind, int? rowSpan = null)
    {
        var p = NewPanel(kind);
        if (rowSpan is int rs) p.RowSpan = Math.Clamp(rs, 1, MaxRowSpan);
        if (IsFullWidth(kind)) p.Span = Columns;
        (p.Row, p.Col) = FindSlot(p.Span, p.RowSpan);
        Panels.Add(p);
        return p;
    }

    /// <summary>パネルの場所と大きさを変える。置けないときは、理由を返して何も変えない</summary>
    public string? TryPlace(PostPanel p, int row, int col, int span, int? rowSpan = null)
    {
        var rs = rowSpan ?? p.RowSpan;
        if (IsFullWidth(p.Kind)) { col = 0; span = Columns; }
        if (IsLocked(p.Kind)) row = p.Row; // 移動できないパネルは、場所を変えない（縦幅だけ変えられる）
        if (span < 1 || span > Columns) return $"横幅は 1〜{Columns} カラムで指定してください。";
        if (rs < 1 || rs > MaxRowSpan) return $"縦幅は 1〜{MaxRowSpan} 段で指定してください。";
        if (row < 0 || row > MaxRow) return $"段は 1〜{MaxRow + 1} で指定してください。";
        if (col < 0 || col + span > Columns) return $"この横幅だと、{Columns} カラムの中に収まりません。";
        if (!IsFree(row, col, span, p, rs)) return "その場所は、ほかのパネルと重なります。";
        p.Row = row; p.Col = col; p.Span = span; p.RowSpan = rs;
        return null;
    }

    /// <summary>
    /// ドラッグ＆ドロップでの移動。空いていればそこへ。ほかのパネルと重なるときは、
    ///  - 重なる相手が1枚で、お互いの場所に収まるなら、場所を入れ替える（大きさが違っても可）
    ///  - 入れ替えられないときは、重なる相手を下へずらす（その下のパネルも、順にずれる）
    /// 移動できないパネル（タイトル）と重なる場合、段の上限を超える場合は、理由を返して何も変えない
    /// </summary>
    public string? TryMove(PostPanel p, int row, int col)
    {
        if (IsLocked(p.Kind)) return "このパネルは移動できません。";
        var plan = Plan(p, row, col);
        if (plan == null) return "その場所には、動かせません（タイトルと重なるか、段の上限を超えます）。";
        foreach (var (panel, r, c) in plan.Moves) (panel.Row, panel.Col) = (r, c);
        return null;
    }

    private record MovePlan(IReadOnlyList<(PostPanel Panel, int Row, int Col)> Moves, PostPanel? Swap);

    /// <summary>縦幅を変える。下のパネルに重なるときは、重なる相手を下へずらす（入れ替えはしない）。置けないときは、理由を返して何も変えない</summary>
    public string? TryResize(PostPanel p, int rowSpan)
    {
        rowSpan = Math.Clamp(rowSpan, 1, MaxRowSpan);
        var old = p.RowSpan;
        p.RowSpan = rowSpan;
        var plan = Plan(p, p.Row, p.Col, allowSwap: false);
        if (plan == null) { p.RowSpan = old; return "縦幅を広げると、段の上限を超えます。"; }
        foreach (var (panel, r, c) in plan.Moves) (panel.Row, panel.Col) = (r, c);
        return null;
    }

    private MovePlan? Plan(PostPanel p, int row, int col, bool allowSwap = true)
    {
        row = Math.Clamp(row, 0, MaxRow);
        col = Math.Clamp(col, 0, Columns - p.Span);
        if (IsFree(row, col, p.Span, p, p.RowSpan)) return new MovePlan([(p, row, col)], null);

        var hit = Panels.Where(o => !ReferenceEquals(o, p) && Overlaps(o, row, col, p.Span, p.RowSpan)).ToList();
        if (hit.Any(h => IsLocked(h.Kind))) return null;

        // 入れ替え：相手が1枚で、互いの場所（左上を合わせる）に、ほかと重ならずに収まる
        if (allowSwap && hit.Count == 1)
        {
            var h = hit[0];
            var pCol = Math.Clamp(col, 0, Columns - p.Span);
            var hCol = Math.Min(p.Col, Columns - h.Span);
            var others = Panels.Where(o => !ReferenceEquals(o, p) && !ReferenceEquals(o, h)).ToList();
            if (IsFree(others, row, pCol, p.Span, p.RowSpan, null) && IsFree(others, p.Row, hCol, h.Span, h.RowSpan, null)
                && !(row < p.Row + h.RowSpan && p.Row < row + p.RowSpan && pCol < hCol + h.Span && hCol < pCol + p.Span))
                return new MovePlan([(p, row, pCol), (h, p.Row, hCol)], h);
        }

        // ずらし：置いたパネルの下へ、重なる相手を押し下げる（押し下げた先で重なるパネルも、順に下へ）
        var pos = Panels.ToDictionary(o => o, o => (o.Row, o.Col));
        pos[p] = (row, col);
        var queue = new Queue<PostPanel>();
        var fixedSet = new HashSet<PostPanel> { p };
        foreach (var h in hit.OrderBy(h => h.Row).ThenBy(h => h.Col)) queue.Enqueue(h);
        var guard = 0;
        while (queue.Count > 0)
        {
            if (++guard > 1000) return null;
            var d = queue.Dequeue();
            // いま置いてあるパネルのうち、d と重なるものの、いちばん下の端の次の段へ
            while (true) // 重なりがなくなるまで、重なる相手の下端へ送る
            {
                var blockers = pos.Where(kv => !ReferenceEquals(kv.Key, d) && fixedSet.Contains(kv.Key)
                        && kv.Value.Row < pos[d].Row + d.RowSpan && pos[d].Row < kv.Value.Row + kv.Key.RowSpan
                        && kv.Value.Col < pos[d].Col + d.Span && pos[d].Col < kv.Value.Col + kv.Key.Span).ToList();
                if (blockers.Count == 0) break;
                pos[d] = (blockers.Max(b => b.Value.Row + b.Key.RowSpan), pos[d].Col);
            }
            if (pos[d].Row + d.RowSpan - 1 > MaxRow + MaxRowSpan) return null;
            fixedSet.Add(d);
            foreach (var o in Panels)
            {
                if (ReferenceEquals(o, d) || fixedSet.Contains(o) || queue.Contains(o)) continue;
                var q = pos[o];
                if (q.Row < pos[d].Row + d.RowSpan && pos[d].Row < q.Row + o.RowSpan && q.Col < pos[d].Col + d.Span && pos[d].Col < q.Col + o.Span)
                {
                    if (IsLocked(o.Kind)) return null;
                    queue.Enqueue(o);
                }
            }
        }
        return new MovePlan(pos.Select(kv => (Panel: kv.Key, Row: kv.Value.Row, Col: kv.Value.Col))
            .Where(m => m.Panel.Row != m.Row || m.Panel.Col != m.Col).ToList(), null);
    }

    /// <summary>ドラッグ中の見た目用：その場所へ動かせるか（置く先の段・列、入れ替える相手。何も変えない。相手がいなければ、ずらしか単なる移動）</summary>
    public (int Row, int Col, PostPanel? Swap) PlanMove(PostPanel p, int row, int col, out bool ok)
    {
        row = Math.Clamp(row, 0, MaxRow);
        col = Math.Clamp(col, 0, Columns - p.Span);
        ok = !IsLocked(p.Kind);
        if (!ok) return (p.Row, p.Col, null);
        var plan = Plan(p, row, col);
        ok = plan != null;
        return (row, col, plan?.Swap);
    }

    /// <summary>その場所へ置くと、ほかのパネルがずれるか（見た目の案内用。入れ替えは含まない）</summary>
    public bool WouldShift(PostPanel p, int row, int col)
    {
        row = Math.Clamp(row, 0, MaxRow);
        col = Math.Clamp(col, 0, Columns - p.Span);
        return !IsLocked(p.Kind) && !IsFree(row, col, p.Span, p, p.RowSpan) && Plan(p, row, col) is { Swap: null };
    }

    /// <summary>全体のカラム数を変える。はみ出す・重なるパネルは、幅を縮めて、空いている場所に置き直す</summary>
    public void SetColumns(int columns)
    {
        Columns = Math.Clamp(columns, MinColumns, MaxColumns);
        var placed = new List<PostPanel>();
        foreach (var p in Panels.OrderBy(p => p.Row).ThenBy(p => p.Col).ToList())
        {
            p.Span = IsFullWidth(p.Kind) ? Columns : Math.Clamp(p.Span, 1, Columns);
            p.RowSpan = Math.Clamp(p.RowSpan, 1, MaxRowSpan);
            p.Row = Math.Max(0, p.Row);
            p.Col = IsFullWidth(p.Kind) ? 0 : Math.Max(0, p.Col);
            if (p.Col + p.Span > Columns || !IsFree(placed, p.Row, p.Col, p.Span, p.RowSpan, null))
                (p.Row, p.Col) = FindSlot(placed, Columns, p.Span, p.RowSpan, null);
            placed.Add(p);
        }
    }

    /// <summary>
    /// 古い設定（版 2 未満）を、いまの形に作り替える：パネルの縦幅を中身の見積りで決め、
    /// 全カラム幅の「タイトル」パネルを1段目に足して、ほかを1段下げる。重なったものは置き直す
    /// </summary>
    public void MigrateToCurrent(string? oldTitle)
    {
        if (Version >= CurrentVersion) return;
        if (Version >= 2)
        {
            // 版 2 → 3：1段の高さを半分にしたので、段の位置と縦幅を2倍にする（見た目は変わらない）
            foreach (var p in Panels)
            {
                p.Row *= 2;
                p.RowSpan = Math.Clamp(p.RowSpan * 2, 1, MaxRowSpan);
            }
            Version = CurrentVersion;
            return;
        }
        foreach (var p in Panels)
        {
            p.RowSpan = EstimateRowSpan(p.Kind, p.ItemCount, p.ItemColumns);
            p.Row = p.Row * 2 + TitleRowSpan;
        }
        var title = NewPanel(PostPanelKind.Title);
        title.Title = string.IsNullOrWhiteSpace(oldTitle) ? null : oldTitle.Trim();
        title.Span = Columns;
        Panels.Insert(0, title);
        Version = CurrentVersion;
    }

    /// <summary>読み込んだ設定の整合をとる（壊れた値・重なりを直す）</summary>
    public void Normalize()
    {
        foreach (var p in Panels)
        {
            p.Height = Math.Clamp(p.Height, 0, 3000);
            p.RowSpan = Math.Clamp(p.RowSpan, 1, MaxRowSpan);
            p.ItemColumns = Math.Clamp(p.ItemColumns, 0, 4); // 0 は自動（パネルの幅に入るだけ並べる）
            p.ViewColumns = Math.Clamp(p.ViewColumns, 0, 4);
            p.Align = Math.Clamp(p.Align, 0, 2);
            p.ItemCount = Math.Clamp(p.ItemCount, 1, MaxItems(p.Kind));
            p.FontSize = p.FontSize <= 0 ? 0 : Math.Clamp(p.FontSize, 8, 96);
            if (p.Transparency is int t) p.Transparency = Math.Clamp(t, 0, 100);
            if (p.Text is { Length: > MaxTextLength }) p.Text = p.Text[..MaxTextLength];
            p.GameIds ??= new();
            if (p.GameIds.Count > MaxPriorityGames) p.GameIds = p.GameIds.Take(MaxPriorityGames).ToList();
        }
        SetColumns(Columns);
    }
}
