using System.IO;
using System.Windows;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// メイン画面のうち、「購入前の作品の整理」（批評空間では購入予定を「ー」にして記録を消し、ツールの一覧からも削除する）を担当する部分
/// </summary>
public partial class MainWindow
{
    private async void CleanupBeforePurchase_Click(object sender, RoutedEventArgs e)
    {
        var candidates = BeforePurchaseCleanup.Candidates(_db.GetAll());
        if (candidates.Count == 0)
        {
            MessageBox.Show(this, "整理できる購入前の作品はありません。", "購入前の作品の整理");
            return;
        }

        var dlg = new BeforePurchaseCleanupWindow(candidates) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Selected.Count == 0) return;
        var selected = dlg.Selected;
        var erase = dlg.EraseContent;

        var withSite = selected.Count(g => g.EgsId != null);
        var withContent = erase ? selected.Count(BeforePurchaseCleanup.HasOwnContent) : 0;
        if (MessageBox.Show(this,
                $"{selected.Count} 件を、批評空間とツールの両方から外します。\n\n" +
                $"・批評空間：{withSite} 件の購入予定を「ー」、購入前得点を空にして登録します（記録が空になると、批評空間から消えます）\n" +
                "・ツール：反映を確認できた作品だけ、一覧から削除します（プレイ時間の記録も消えます）\n" +
                (erase ? $"・得点・感想：{withContent} 件の得点・一言感想・長文感想も空にします。書いた内容は失われ、元に戻せません\n" : "") +
                "\n批評空間側は、あとから元に戻せません。続けますか？",
                "購入前の作品の整理", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (!BeginEgsWork(lockDetail: true)) return;
        int deleted = 0, notConfirmed = 0, notTried = 0;
        try
        {
            var backup = DbBackup.Snapshot(AppPaths.Database, Path.Combine(AppPaths.Dir, "backups"), "before_cleanup");
            if (backup == null &&
                MessageBox.Show(this, "作品の一覧の控えを残せませんでした。控えなしで続けますか？", "購入前の作品の整理",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            for (var i = 0; i < selected.Count; i++)
            {
                var g = _db.Get(selected[i].Id);
                if (g == null) continue;
                StatusText.Text = $"購入前の作品を整理中（{i + 1}/{selected.Count}）：{g.Title}";

                if (g.EgsId != null)
                {
                    BeforePurchaseCleanup.Prepare(g, erase);
                    _db.Update(g);
                    if (!await PushToEgsAsync(g.Id))
                    {
                        notTried = selected.Count - i - 1; // 失敗したら、ここで止める（失敗の画面は PushToEgsAsync が出している）
                        notConfirmed++;
                        break;
                    }
                    var fresh = _db.Get(g.Id);
                    if (fresh == null) continue;
                    if (!BeforePurchaseCleanup.IsReflected(fresh, erase))
                    {
                        notConfirmed++;
                        continue;
                    }
                }

                _db.Delete(g.Id);
                deleted++;
                await Task.Delay(1000); // 続けて通信しすぎないよう、間をあける
            }
        }
        finally
        {
            EndEgsWork();
            Reload();
        }

        StatusText.Text = $"購入前の作品を整理しました：削除 {deleted} 件";
        var rest = notConfirmed + notTried;
        MessageBox.Show(this,
            $"削除した作品：{deleted} 件" +
            (rest == 0 ? "" : $"\n一覧に残した作品：{rest} 件（批評空間への反映を確認できなかった、または途中で止めたため）\n" +
                              "もう一度、整理の画面から選び直せます。"),
            "購入前の作品の整理");
    }
}
