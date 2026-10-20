using System.Windows;
using System.Windows.Controls;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// 同期でツールと批評空間の内容が食い違ったときに、どちらを使うか聞く。
/// 「以降の同じ種類の食い違いにも同じ選択を使う」に☑すると、同期が終わるまで聞かない。
/// </summary>
public sealed class DialogConflictResolver(Window owner) : IConflictResolver
{
    private readonly Dictionary<Conflict, bool> _remembered = new();

    private static string Label(Conflict kind) => kind switch
    {
        Conflict.ClearDate => "クリア日",
        Conflict.Score => "得点",
        _ => "長文感想",
    };

    public bool UseTool(Game game, Conflict kind, string toolText, string siteText)
    {
        if (_remembered.TryGetValue(kind, out var remembered)) return remembered;

        var label = Label(kind);
        var dialog = new ChoiceWindow(
            $"{label}の食い違い",
            $"「{game.Title}」の{label}が、ツールと批評空間で違います。\n\n" +
            $"【ツール】\n{toolText}\n\n【批評空間】\n{siteText}",
            "ツールを使う（批評空間に反映）", "批評空間を使う（ツールに取り込む）",
            $"以降の「{label}」の食い違いにも、同じ選択を使う") { Owner = owner };
        dialog.ShowDialog();
        if (dialog.Remember) _remembered[kind] = dialog.FirstChosen;
        return dialog.FirstChosen;
    }
}

/// <summary>2つの選択肢と「以降も同じ」のチェックだけの、小さな確認ウィンドウ</summary>
internal sealed class ChoiceWindow : Window
{
    private readonly CheckBox _remember;
    private bool _chosen;

    public bool FirstChosen { get; private set; }
    public bool Remember => _remember.IsChecked == true;

    public ChoiceWindow(string title, string message, string first, string second, string rememberText)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var firstButton = new Button { Content = first, Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
        var secondButton = new Button { Content = second, Padding = new Thickness(12, 6, 12, 6) };
        firstButton.Click += (_, _) => Choose(true);
        secondButton.Click += (_, _) => Choose(false);
        _remember = new CheckBox { Content = rememberText, Margin = new Thickness(0, 12, 0, 12) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(firstButton);
        buttons.Children.Add(secondButton);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_remember);
        panel.Children.Add(buttons);
        Content = panel;

        // どちらかを選ぶまで閉じさせない（×で閉じて、意図しない側が選ばれるのを防ぐ）
        Closing += (_, e) => e.Cancel = !_chosen;
    }

    private void Choose(bool isFirst)
    {
        FirstChosen = isFirst;
        _chosen = true;
        Close();
    }
}
