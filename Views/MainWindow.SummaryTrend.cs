using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// レポートの「傾向」欄：年ごとの推移・ブランド別・得点の分布・ジャンル別。
/// 上で選んだ期間には関係なく、登録している全作品・全期間を見る。
/// ジャンルは批評空間から取得する必要があるので、「ジャンルを取得」ボタンで取得する（取得済みの作品だけ集計する）。
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<YearTrend> _trendYears = Array.Empty<YearTrend>();
    private IReadOnlyList<GroupTrend> _trendBrands = Array.Empty<GroupTrend>();
    private IReadOnlyList<GroupTrend> _trendGenres = Array.Empty<GroupTrend>();
    private IReadOnlyList<ScoreBand> _trendBands = Array.Empty<ScoreBand>();
    private (int Fetched, int Total) _trendCoverage;
    private int _trendNeedFetch;
    private Dictionary<int, GameTraits> _trendTraits = new();
    private bool _trendFetching;
    private string _trendFetchStatus = "";

    /// <summary>傾向の元になる数字を読み直す（期間を読み直すたびに）</summary>
    private void LoadSummaryTrend(IReadOnlyList<SessionRow> allSessions)
    {
        var games = _summaryGames;
        if (!_trendFetching) _trendTraits = TraitStore.Load();
        _trendYears = TrendStats.ByYear(allSessions, games);
        _trendBrands = TrendStats.ByBrand(games);
        _trendBands = TrendStats.ScoreBands(games);
        _trendGenres = TrendStats.ByGenre(games, _trendTraits);
        _trendCoverage = TrendStats.GenreCoverage(games, _trendTraits);
        _trendNeedFetch = TrendStats.GenreNeedFetch(games, _trendTraits).Count;
    }

    private void DrawSummaryTrend()
    {
        var (_, title, body) = _summaryExtra["trend"];
        body.Children.Clear();
        title.Text = "遊んだ全作品の傾向（上で選んだ期間には関係なく、全期間）";

        // 年ごとの推移
        body.Children.Add(TrendHeading("年ごとの推移"));
        if (_trendYears.Count == 0) body.Children.Add(TrendNote("まだプレイの記録がありません。"));
        else
        {
            var max = _trendYears.Max(y => y.Seconds);
            foreach (var y in _trendYears)
            {
                var score = y.AvgScore is double a ? $"　平均 {a:0.#}点（{y.Scored}本）" : "";
                body.Children.Add(TrendBarRow($"{y.Year}年", y.Seconds, max,
                    $"{SummaryCalculator.FormatDuration(y.Seconds)}　{y.Games}本遊んだ　クリア {y.Clears}本{score}"));
            }
            body.Children.Add(TrendNote("プレイ時間と遊んだ作品はプレイした年、クリアと平均点はクリア日の年で数えています"));
        }

        // ブランド別
        body.Children.Add(TrendHeading("ブランド別（遊んだ作品が多い順・上位10）"));
        if (_trendBrands.Count == 0) body.Children.Add(TrendNote("ブランドの分かる作品がありません。"));
        else
        {
            var max = _trendBrands.Max(b => (double)b.Games);
            foreach (var b in _trendBrands) body.Children.Add(TrendBarRow(b.Name, b.Games, max, GroupText(b), 150));
        }

        // 得点の分布
        body.Children.Add(TrendHeading("得点の分布（遊んだ作品のうち得点を付けたもの）"));
        var maxBand = _trendBands.Count == 0 ? 0 : _trendBands.Max(b => (double)b.Count);
        if (maxBand == 0) body.Children.Add(TrendNote("得点を付けた作品がありません。"));
        else foreach (var b in _trendBands) body.Children.Add(TrendBarRow(b.Label, b.Count, maxBand, $"{b.Count}本"));

        // ジャンル別
        body.Children.Add(TrendHeading("ジャンル別（遊んだ作品が多い順・上位10）"));
        body.Children.Add(TrendNote($"ジャンルは批評空間から取得します。取得済み：遊んだ作品 {_trendCoverage.Total} 本のうち {_trendCoverage.Fetched} 本"));
        if (_trendGenres.Count == 0 && _trendCoverage.Fetched == 0) body.Children.Add(TrendNote("まだ取得していません。"));
        else
        {
            var max = _trendGenres.Count == 0 ? 0 : _trendGenres.Max(g => (double)g.Games);
            foreach (var g in _trendGenres) body.Children.Add(TrendBarRow(g.Name, g.Games, max, GroupText(g), 150));
        }
        var fetch = new Button
        {
            Content = _trendFetching ? "取得しています…" : _trendNeedFetch == 0 ? "ジャンルは取得済みです" : $"ジャンルを取得（{_trendNeedFetch}本）",
            IsEnabled = !_trendFetching && _trendNeedFetch > 0, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 3, 12, 3), ToolTip = "遊んだ作品のジャンルを、批評空間から取得します（取得は1回だけで、結果は保存します）",
        };
        fetch.Click += async (_, _) => await FetchTrendGenresAsync();
        body.Children.Add(fetch);
        if (_trendFetchStatus != "") body.Children.Add(TrendNote(_trendFetchStatus));
    }

    private static string GroupText(GroupTrend g)
    {
        var score = g.AvgScore is double a ? $"　平均 {a:0.#}点（{g.Scored}本）" : "";
        return $"{g.Games}本　クリア {g.Clears}本　{SummaryCalculator.FormatDuration(g.Seconds)}{score}";
    }

    private async Task FetchTrendGenresAsync()
    {
        if (_trendFetching) return;
        var need = TrendStats.GenreNeedFetch(_summaryGames, _trendTraits);
        if (need.Count == 0) return;
        _trendFetching = true;
        _trendFetchStatus = "";
        DrawSummaryTrend();
        try
        {
            var got = await _egs.GetTraitsAsync(need, (done, all) => Dispatcher.Invoke(() =>
            {
                _trendFetchStatus = $"取得しています… {done} / {all} 本";
                DrawSummaryTrend();
            }));
            // 次の1本で取得済みのものも残すため、保存済みの内容に足して保存する
            var merged = TraitStore.Load();
            foreach (var t in got) merged[t.EgsId] = t;
            TraitStore.Save(merged.Values);
            _trendTraits = merged;
            _trendFetchStatus = $"{got.Count} 本のジャンルを取得しました";
        }
        catch (Exception ex)
        {
            Log.Warn("ジャンルの取得に失敗しました", ex);
            _trendFetchStatus = "取得できませんでした：" + ex.Message;
        }
        finally
        {
            _trendFetching = false;
            LoadSummaryTrend(_db.GetSessions(DateTime.MinValue, DateTime.Today.AddDays(1)));
            DrawSummaryTrend();
        }
    }

    private static TextBlock TrendHeading(string text) =>
        new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap };

    private static TextBlock TrendNote(string text) =>
        new() { Text = text, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };

    /// <summary>ラベル・棒・説明の1行（棒の長さは max に対する割合）</summary>
    private static UIElement TrendBarRow(string label, double value, double max, string text, double labelWidth = 64)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label });
        var frac = max <= 0 ? 0 : value / max;
        var bar = new Border
        {
            Height = 10, CornerRadius = new CornerRadius(2), Background = BarBrush, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Width = Math.Max(value > 0 ? 2 : 0, 100 * frac), ToolTip = text,
        };
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        var t = new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 2);
        row.Children.Add(t);
        return row;
    }
}
