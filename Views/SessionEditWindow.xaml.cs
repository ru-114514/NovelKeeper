using System.Windows;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>プレイ時間の記録を1件、追加または編集する</summary>
public partial class SessionEditWindow : Window
{
    public DateTime ResultStart { get; private set; }
    public long ResultSeconds { get; private set; }

    /// <param name="existing">編集するとき、その記録（追加なら null）</param>
    public SessionEditWindow(string gameTitle, SessionEntry? existing)
    {
        InitializeComponent();
        Heading.Text = (existing == null ? "記録を追加：" : "記録を編集：") + gameTitle;

        if (existing == null)
        {
            DatePick.SelectedDate = DateTime.Today;
            TimeBox.Text = "00:00";
        }
        else
        {
            DatePick.SelectedDate = existing.Start.Date;
            TimeBox.Text = existing.Start.ToString("HH:mm");
            HoursBox.Text = (existing.Seconds / 3600).ToString();
            MinutesBox.Text = (existing.Seconds % 3600 / 60).ToString();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DatePick.SelectedDate is not DateTime date)
        {
            ErrorText.Text = "日付を選んでください。";
            return;
        }
        if (!ManualSessionInput.TryBuild(date, TimeBox.Text, HoursBox.Text, MinutesBox.Text, DateTime.Now,
                out var start, out var seconds, out var error))
        {
            ErrorText.Text = error;
            return;
        }
        ResultStart = start;
        ResultSeconds = seconds;
        DialogResult = true;
    }
}
