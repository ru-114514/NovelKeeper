using System.Windows;
using System.Windows.Controls;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// 「放置作品の見直し」：長く遊んでいないプレイ中の作品と、発売からたっても手つかずの未プレイの作品を並べ、
/// 作品ごとに 継続（そのまま）／優先／除外（未プレイ：積みゲーに数えない）／中断（プレイ中）を選んで、まとめて反映する。
/// 候補の判定は StaleReview（画面に触れない）に任せ、ここは画面だけを受け持つ
/// </summary>
public class StaleReviewWindow : Window
{
    private const string Keep = "継続", Priority = "優先", Unprioritize = "優先を外す", Exclude = "除外", Suspend = "中断";

    private readonly EgsConfig _cfg;
    private readonly List<Game> _games;
    private readonly IReadOnlyDictionary<long, DateTime> _lastPlayed;
    private readonly Action<List<Game>> _addToPriority;
    private readonly Action<Game> _drop;
    private readonly Dictionary<long, string> _choices = new();

    private readonly TextBox _days = new() { Width = 46, Text = StaleReview.DefaultPlayingDays.ToString(), HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBox _years = new() { Width = 36, Text = StaleReview.DefaultUnplayedYears.ToString(), HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public bool Changed { get; private set; }

    public StaleReviewWindow(Window owner, EgsConfig cfg, List<Game> games, IReadOnlyDictionary<long, DateTime> lastPlayed,
        Action<List<Game>> addToPriority, Action<Game> drop)
    {
        _cfg = cfg; _games = games; _lastPlayed = lastPlayed; _addToPriority = addToPriority; _drop = drop;
        Owner = owner;
        Title = "放置作品の見直し";
        Width = 820; Height = 660; MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildUi();
        Refresh();
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(14) };

        var head = new TextBlock
        {
            Text = "長く遊んでいないプレイ中の作品と、登録から時間がたっても未プレイの作品です。作品ごとに選んで「反映する」を押します（選ばなければ「継続」でそのままです）。\n" +
                   "・優先：投稿設定の「今後優先したい作品」に入れます　・除外（未プレイ）：状態は変えず、積みゲーの本数に数えません　・中断（プレイ中）：状態を「中断」にします（批評空間へは次の同期で反映）",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var cond = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        cond.Children.Add(Label("プレイ中：最後に遊んでから"));
        cond.Children.Add(_days);
        cond.Children.Add(Label("日以上　　未プレイ：登録から"));
        cond.Children.Add(_years);
        cond.Children.Add(Label("年以上"));
        var show = new Button { Content = "再表示", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        show.Click += (_, _) => Refresh();
        cond.Children.Add(show);
        DockPanel.SetDock(cond, Dock.Top);
        root.Children.Add(cond);

        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new WrapPanel();
        var apply = new Button { Content = "反映する", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 6, 0) };
        apply.Click += Apply_Click;
        var restore = new Button { Content = "除外した作品を戻す…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "「積みゲーに数えない」にした作品を、数える状態に戻します" };
        restore.Click += Restore_Click;
        var close = new Button { Content = "閉じる", Padding = new Thickness(16, 4, 16, 4) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(apply);
        buttons.Children.Add(restore);
        buttons.Children.Add(close);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
        return root;
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };

    private void Refresh()
    {
        var days = int.TryParse(_days.Text.Trim(), out var d) && d > 0 ? d : StaleReview.DefaultPlayingDays;
        var years = int.TryParse(_years.Text.Trim(), out var y) && y > 0 ? y : StaleReview.DefaultUnplayedYears;
        var items = StaleReview.Candidates(_games, _lastPlayed, DateTime.Today, days, years, _cfg.BacklogExcludedIds.ToHashSet());

        _list.Children.Clear();
        if (items.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "見直しの候補はありません", Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) });
            return;
        }
        AddHeader("プレイ中", items.Where(i => i.Game.Status == GameStatus.Playing).ToList(), "最後に遊んだ日", [Keep, Priority, Suspend]);
        AddHeader("未プレイ", items.Where(i => i.Game.Status == GameStatus.Unplayed).ToList(), "登録日（なければ発売日）", [Keep, Priority, Exclude]);
    }

    private void AddHeader(string name, List<StaleItem> items, string sinceLabel, string[] options)
    {
        if (items.Count == 0) return;
        _list.Children.Add(new TextBlock { Text = $"{name}（{items.Count}本）", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4) });
        foreach (var item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

            var since = item.Since is DateTime s ? $"{sinceLabel} {s:yyyy/MM/dd}（{Elapsed(item.Days!.Value)}）" : $"{sinceLabel}：記録なし";
            if (item.Game.IsPriority) since += " ★優先に追加済み";
            row.Children.Add(new TextBlock { Text = item.Game.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = item.Game.Title });
            var info = new TextBlock { Text = since, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            var median = new TextBlock { Text = item.Game.Median is int m ? $"中央値 {m}" : "", Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Grid.SetColumn(median, 2);
            row.Children.Add(median);

            if (item.Game.IsPriority) options = options.Select(o => o == Priority ? Unprioritize : o).ToArray(); // 追加済みなら、外す選択肢にする
            var box = new ComboBox { ItemsSource = options, SelectedItem = _choices.GetValueOrDefault(item.Game.Id, Keep) };
            if (box.SelectedItem == null || !options.Contains((string)box.SelectedItem)) box.SelectedItem = Keep;
            var id = item.Game.Id;
            box.SelectionChanged += (_, _) => _choices[id] = (string)(box.SelectedItem ?? Keep);
            Grid.SetColumn(box, 3);
            row.Children.Add(box);
            _list.Children.Add(row);
        }
    }

    private static string Elapsed(int days) => days >= 365 ? $"{days / 365}年{days % 365 / 30}か月前" : days >= 30 ? $"{days / 30}か月前" : $"{days}日前";

    /// <summary>「積みゲーに数えない」にした作品の一覧を開き、チェックした作品を数える状態に戻す</summary>
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var byId = _games.ToDictionary(g => g.Id);
        var excluded = _cfg.BacklogExcludedIds.Where(byId.ContainsKey).Select(id => byId[id])
            .OrderBy(g => g.Title, StringComparer.CurrentCulture).ToList();
        if (excluded.Count == 0)
        {
            MessageBox.Show(this, "積みゲーに数えない作品はありません。", "除外した作品", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new Window
        {
            Owner = this, Title = "除外した作品を戻す", Width = 520, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var root = new DockPanel { Margin = new Thickness(14) };
        var head = new TextBlock { Text = "戻す作品にチェックを入れてください。戻すと、積みゲーの本数に数え、見直しの候補にも出ます。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "戻す", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var boxes = new List<(CheckBox Box, long Id)>();
        var panel = new StackPanel();
        foreach (var g in excluded)
        {
            var cb = new CheckBox { Content = g.Title, Margin = new Thickness(0, 2, 0, 2) };
            boxes.Add((cb, g.Id));
            panel.Children.Add(cb);
        }
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });
        win.Content = root;

        ok.Click += (_, _) =>
        {
            var back = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToHashSet();
            if (back.Count > 0)
            {
                _cfg.BacklogExcludedIds.RemoveAll(back.Contains);
                _cfg.Save();
                Game.BacklogExcludedIds = _cfg.BacklogExcludedIds.ToHashSet();
                Changed = true;
                _status.Text = $"{back.Count}本を、積みゲーに数える状態に戻しました";
                Refresh();
            }
            win.Close();
        };
        win.ShowDialog();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var byId = _games.ToDictionary(g => g.Id);
        var priority = new List<Game>();
        var excluded = 0; var dropped = 0; var unprioritized = 0;
        foreach (var (id, choice) in _choices.ToList())
        {
            if (choice == Keep || !byId.TryGetValue(id, out var g)) continue;
            switch (choice)
            {
                case Priority: priority.Add(g); break;
                case Unprioritize:
                    foreach (var p in _cfg.PostLayout.Panels.Where(p => p.Kind == PostPanelKind.Priority)) p.GameIds.Remove(id);
                    unprioritized++;
                    break;
                case Exclude:
                    if (!_cfg.BacklogExcludedIds.Contains(id)) _cfg.BacklogExcludedIds.Add(id);
                    excluded++;
                    break;
                case Suspend:
                    _drop(g);
                    g.Status = GameStatus.Dropped;
                    dropped++;
                    break;
            }
            _choices.Remove(id);
        }
        if (priority.Count > 0) _addToPriority(priority);
        foreach (var g in priority) _choices.Remove(g.Id);
        if (excluded + unprioritized > 0) _cfg.Save();
        Game.BacklogExcludedIds = _cfg.BacklogExcludedIds.ToHashSet();
        Game.PriorityIds = _cfg.PostLayout.Panels.Where(p => p.Kind == PostPanelKind.Priority).SelectMany(p => p.GameIds).ToHashSet();
        var total = priority.Count + unprioritized + excluded + dropped;
        Changed = Changed || total > 0;
        _status.Text = total == 0
            ? "変更はありません（すべて「継続」です）"
            : $"反映しました（優先 {priority.Count}本、優先を外す {unprioritized}本、除外 {excluded}本、中断 {dropped}本）" +
              (priority.Count > 0 ? "　優先は、投稿設定の「今後優先したい作品」パネルに入れました（一覧の「特例」列に★優先と出ます）" : "");
        Refresh();
    }
}
