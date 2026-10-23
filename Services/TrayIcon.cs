using System.Drawing;
using System.Windows.Forms;

namespace ErogeLauncher.Services;

/// <summary>
/// 通知領域（タスクバー右下）のアイコン。
/// WPF には通知領域の部品がないので、Windows Forms の NotifyIcon を使う。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("設定", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Text = "NovelKeeper",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => _balloonClicked?.Invoke();
    }

    private Action? _balloonClicked;

    /// <summary>通知（バルーン）を表示する。onClick は、その通知をクリックしたときの動作</summary>
    public void Notify(string title, string text, Action? onClick = null)
    {
        _balloonClicked = onClick; // 直前の通知の動作を、あとの通知に持ち越さない
        _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
    }

    /// <summary>アイコンにマウスを乗せたときの文字（63文字まで）</summary>
    public void SetText(string text) =>
        _icon.Text = text.Length > 63 ? text[..63] : text;

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path != null && Icon.ExtractAssociatedIcon(path) is Icon icon) return icon;
        }
        catch (Exception ex) { Log.Warn("アイコンを読み込めませんでした", ex); }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
