using System.IO;
using System.Windows;
using System.Windows.Controls;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// メイン画面のうち、詳細欄の「得点登録フォームの項目」・作品の画像・
/// ログイン設定タブ・ゲーム起動設定タブを担当する部分
/// </summary>
public partial class MainWindow
{
    // ================================================================
    // 詳細欄：得点登録フォームの項目
    // ================================================================

    private bool _reviewBoxesReady;

    // ---- 詳細欄の編集中の状態 ----
    private bool _detailDirty;    // 詳細欄に、まだ保存していない変更がある
    private long? _detailGameId;  // 詳細欄に表示している作品
    private bool _detailLocked;   // 同期・取り込みの間は、詳細欄を編集できないようにする
    private bool _egsBusy;        // 批評空間と通信中（同期・取り込み・反映）

    /// <summary>
    /// 批評空間との通信を始める。すでに通信中なら false（同時に動くと、古い内容で上書きしてしまうため）。
    /// lockDetail のときは、終わるまで詳細欄を編集できないようにする。
    /// </summary>
    private bool BeginEgsWork(bool lockDetail, bool quiet = false)
    {
        if (_egsBusy)
        {
            if (!quiet) MessageBox.Show("批評空間と通信中です。終わるまでお待ちください。", "NovelKeeper");
            return false;
        }
        if (lockDetail && _detailDirty)
        {
            var ans = MessageBox.Show("詳細欄に、保存していない変更があります。\n変更を破棄して続けますか？\n（［いいえ］で中止して、先に保存できます）",
                "未保存の変更", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return false;
            _detailDirty = false;
            ShowDetail(Selected);
        }
        _egsBusy = true;
        if (lockDetail)
        {
            _detailLocked = true;
            DetailPanel.IsEnabled = false;
        }
        return true;
    }

    private void EndEgsWork()
    {
        _egsBusy = false;
        if (_detailLocked)
        {
            _detailLocked = false;
            DetailPanel.IsEnabled = Selected != null;
        }
    }

    /// <summary>詳細欄の入力欄が変わったら「未保存の変更あり」にする</summary>
    private void HookDetailDirty()
    {
        void Mark() { if (!_loadingDetail) _detailDirty = true; }
        foreach (var box in new[] { ScoreBox, TotalPlayTimeBox, FunTimeBox, BeforeScoreBox, PriceBox, PlatformMemoBox, HitokotoBox, MemoBox })
            box.TextChanged += (_, _) => Mark();
        foreach (var check in new[] { PlayedCheck, PossessionCheck, SageCheck, NetabareCheck, ExcludeClearCheck })
            check.Click += (_, _) => Mark();
        foreach (var combo in new[] { StatusBox, OkazuBox, PurchaseWillBox, PlatformBox, OperationBox })
            combo.SelectionChanged += (_, _) => Mark();
        foreach (var date in new[] { StartDatePicker, ClearDatePicker, RegisteredPicker })
            date.SelectedDateChanged += (_, _) => Mark();
    }

    private bool _formattingPrice;

    /// <summary>購入額の欄：入力に合わせて、金額に応じたカンマを自動で入れる（数字以外は取り除く。全角の数字も半角にする）</summary>
    private void PriceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_formattingPrice) return;
        var box = (TextBox)sender;
        var digitsBeforeCaret = 0;
        for (var i = 0; i < Math.Min(box.CaretIndex, box.Text.Length); i++)
            if (IsDigit(box.Text[i])) digitsBeforeCaret++;

        var digits = new string(box.Text.Where(IsDigit).Select(c => c >= '０' ? (char)('0' + (c - '０')) : c).ToArray()).TrimStart('0');
        if (digits.Length > 8) digits = digits[..8]; // 上限 99,999,999
        var formatted = digits.Length == 0 ? "" : long.Parse(digits).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        if (box.Text.Trim() != "" && digits.Length == 0 && box.Text.Any(c => c == '0' || c == '０')) formatted = "0"; // 「0」だけは 0 円として残す

        if (formatted == box.Text) return;
        _formattingPrice = true;
        try
        {
            box.Text = formatted;
            // カーソルは、同じ数字の後ろに戻す
            var pos = 0;
            for (var seen = 0; pos < formatted.Length && seen < digitsBeforeCaret; pos++)
                if (char.IsDigit(formatted[pos])) seen++;
            box.CaretIndex = pos;
        }
        finally { _formattingPrice = false; }

        static bool IsDigit(char c) => c is >= '0' and <= '9' or >= '０' and <= '９';
    }

    /// <summary>選択欄に、批評空間と同じ選択肢を入れる</summary>
    private void InitReviewBoxes()
    {
        if (_reviewBoxesReady) return;
        _reviewBoxesReady = true;
        HookDetailDirty();
        foreach (var (v, label) in Review.OkazuOptions) OkazuBox.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        foreach (var (v, label) in Review.PurchaseWillOptions) PurchaseWillBox.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        foreach (var (v, label) in Review.PlatformOptions) PlatformBox.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        foreach (var (v, label) in Review.OperationOptions) OperationBox.Items.Add(new ComboBoxItem { Content = label, Tag = v });

        // 開いているグループを復元する
        foreach (var exp in new[] { ExpPlay, ExpPlatform, ExpReview })
            exp.IsExpanded = _cfg.ExpandedSections.Contains((string)exp.Tag);
    }

    private static void SelectByTag(ComboBox box, object? value)
    {
        var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));
        if (item == null && value != null && value as string != "")
        {
            // 選択肢にない値（批評空間で選択肢が増えたときなど）は、消さないよう選択肢に足して表示する
            item = new ComboBoxItem { Content = $"{value}", Tag = value };
            box.Items.Add(item);
        }
        box.SelectedItem = item ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    /// <summary>作品の内容を、詳細欄の各項目に表示する</summary>
    private void ShowReview(Game? g)
    {
        InitReviewBoxes();
        var r = g?.Review ?? new Review();
        PossessionCheck.IsChecked = (g?.Review.Possession ?? g?.EgsPossession) == true;
        UnreleasedBadge.Visibility = g?.IsUnreleased == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateAdminBadge(g);
        StartDatePicker.SelectedDate = r.StartDate;
        TotalPlayTimeBox.Text = r.TotalPlayTime?.ToString() ?? "";
        FunTimeBox.Text = r.FunTime?.ToString() ?? "";
        SelectByTag(OkazuBox, r.Okazu);
        SelectByTag(PurchaseWillBox, r.PurchaseWill ?? "");
        BeforeScoreBox.Text = r.BeforeScore?.ToString() ?? "";
        PriceBox.Text = g?.PriceText ?? "";
        RegisteredPicker.SelectedDate = g?.EgsRegistered;
        SelectByTag(PlatformBox, r.Platform);
        SelectByTag(OperationBox, r.Operation);
        PlatformMemoBox.Text = r.PlatformMemo;
        HitokotoBox.Text = r.Hitokoto;
        SageCheck.IsChecked = r.Sage;
        MemoBox.Text = r.Memo;
        NetabareCheck.IsChecked = r.Netabare;
        ExcludeClearCheck.IsChecked = r.ExcludeFromClearCount;
        ShowScoreItems(g);
    }

    /// <summary>詳細欄の各項目を読み取る。入力が正しくなければ、メッセージを出して null を返す</summary>
    private Review? ReadReviewFromUi(Game g)
    {
        int? Num(TextBox box, string label, int min, int max, out bool ok)
        {
            ok = true;
            var t = box.Text.Trim();
            if (t == "") return null;
            if (int.TryParse(t, out var v) && v >= min && v <= max) return v;
            MessageBox.Show($"{label}は {min}～{max} の整数で入力してください。", "入力エラー");
            ok = false;
            return null;
        }

        var total = Num(TotalPlayTimeBox, "総プレイ時間", 0, 100000, out var ok1);
        var fun = Num(FunTimeBox, "面白さが分かるまでのプレイ時間", 0, 100000, out var ok2);
        var before = Num(BeforeScoreBox, "購入前得点", 0, 100, out var ok3);
        if (!ok1 || !ok2 || !ok3) return null;

        var r = g.Review.Clone();
        // 所持：もともと不明（null）で☑もないなら、不明のままにする
        var possession = PossessionCheck.IsChecked == true;
        if (possession) r.Possession = true;
        else r.Possession = (g.Review.Possession ?? g.EgsPossession) == null ? (bool?)null : false;
        r.TotalPlayTime = total;
        r.FunTime = fun;
        r.Okazu = OkazuBox.SelectedItem is ComboBoxItem { Tag: int oz } ? oz : null;
        r.PurchaseWill = PurchaseWillBox.SelectedItem is ComboBoxItem { Tag: string pw } ? pw : "";
        r.BeforeScore = before;
        r.StartDate = StartDatePicker.SelectedDate?.Date;
        r.Platform = PlatformBox.SelectedItem is ComboBoxItem { Tag: int p } ? p : null;
        r.Operation = OperationBox.SelectedItem is ComboBoxItem { Tag: int o } ? o : null;
        r.PlatformMemo = PlatformMemoBox.Text;
        r.Hitokoto = HitokotoBox.Text.Trim();
        r.Sage = SageCheck.IsChecked == true;
        r.ExcludeFromClearCount = ExcludeClearCheck.IsChecked == true;
        r.Memo = MemoBox.Text;
        r.Netabare = NetabareCheck.IsChecked == true;
        return r;
    }

    private void FillStartDate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.FirstPlayed is DateTime d) StartDatePicker.SelectedDate = d.Date;
        else MessageBox.Show("この作品は、まだプレイ時間の記録がありません。", "ゲーム開始日");
    }

    /// <summary>計測したプレイ時間の記録を、追加・編集・削除する画面を開く</summary>
    private void EditPlayTime_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Game g) return;
        if (_activeGameIds.Contains(g.Id))
        {
            MessageBox.Show(this, "この作品は、いま計測中です。\nゲームを終了して、計測が終わってから補正してください。", "プレイ時間の補正");
            return;
        }

        // 補正のあと一覧を読み直すと、詳細欄の編集中の内容が消えるので、先に確かめる
        if (_detailDirty &&
            MessageBox.Show(this, "詳細欄に、保存していない変更があります。\n補正を開くと、その変更は破棄されます。続けますか？\n（［いいえ］で中止して、先に保存できます）",
                "未保存の変更", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var dlg = new PlayTimeEditWindow(_db, g) { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Changed) return;

        _detailDirty = false;
        Reload(); // 合計時間や「初回起動日」が変わる
        if (ReferenceEquals(MainTabs.SelectedItem, SummaryTab)) RefreshSummary();
        StatusText.Text = $"プレイ時間の記録を補正しました：{g.Title}";

        // 総プレイ時間（批評空間に送る値）が、補正した計測時間とずれたら、入れ直すか聞く
        if (_db.Get(g.Id) is { } fresh && fresh.TotalSeconds > 0 && fresh.Review.TotalPlayTime is int current)
        {
            var hours = Math.Max(1, (int)Math.Round(fresh.TotalSeconds / 3600.0));
            if (hours != current &&
                MessageBox.Show(this, $"総プレイ時間の欄は {current} 時間ですが、補正後の計測時間は {hours} 時間（{Game.Clock(fresh.TotalSeconds)}）です。\n\n" +
                                      $"総プレイ時間の欄を {hours} 時間に入れ直しますか？\n（入れ直したあと、「保存」を押すと批評空間にも反映されます）",
                    "総プレイ時間", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                TotalPlayTimeBox.Text = hours.ToString();
        }
    }

    private void FillTotalTime_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Game g || g.TotalSeconds == 0)
        {
            MessageBox.Show("この作品は、まだプレイ時間の記録がありません。", "総プレイ時間");
            return;
        }
        TotalPlayTimeBox.Text = Math.Max(1, (int)Math.Round(g.TotalSeconds / 3600.0)).ToString();
    }

    private void DetailExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (!_reviewBoxesReady || sender is not Expander { Tag: string key } exp) return;
        _cfg.ExpandedSections.Remove(key);
        if (exp.IsExpanded) _cfg.ExpandedSections.Add(key);
        _cfg.Save();
    }

    // ================================================================
    // 詳細欄：作品の画像
    // ================================================================

    private GameImageService? _imagesField;
    private GameImageService Images => _imagesField ??= new GameImageService(_egs);
    private CancellationTokenSource? _imageCts;

    private async void ShowImage(Game? g)
    {
        _imageCts?.Cancel();
        DetailImage.Source = null;
        DetailImage.Visibility = Visibility.Collapsed;
        DetailImageInfo.Visibility = Visibility.Collapsed;
        DetailImageBox.Visibility = g?.EgsId != null ? Visibility.Visible : Visibility.Collapsed;
        if (g?.EgsId is not int egsId) return;
        if (!_cfg.ShowImages && GameImageService.Custom(egsId) == null)
        {
            // オフのときは、通信せず、差し替えた画像だけを表示する
            ShowImageInfo("画像の表示はオフです");
            return;
        }

        // 保存済みならすぐ表示。なければ、少し待ってから取得する（一覧を素早く移動しているときに取りに行かない）
        var path = GameImageService.Cached(egsId);
        if (path == null)
        {
            var cts = _imageCts = new CancellationTokenSource();
            ShowImageInfo("画像を取得中…");
            try
            {
                await Task.Delay(600, cts.Token);
                path = await Images.GetAsync(egsId, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch { path = null; }
            if (cts.IsCancellationRequested || Selected?.Id != g.Id) return;
        }

        if (path == null)
        {
            ShowImageInfo("画像が見つかりませんでした");
            return;
        }
        try
        {
            DetailImage.Source = ImageFile.Load(path, decodeWidth: 580);
            DetailImage.Visibility = Visibility.Visible;
            DetailImageInfo.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn("画像を表示できませんでした", ex);
            GameImageService.Forget(egsId); // 壊れた画像は消して、次回取り直す
            ShowImageInfo("画像を表示できませんでした");
        }
    }

    /// <summary>画像がないときの枠の文。画像ファイルをドロップして差し替えられることも添える</summary>
    private void ShowImageInfo(string message)
    {
        DetailImageInfo.Text = message + "\n画像ファイルをここにドラッグ＆ドロップして差し替えできます";
        DetailImageInfo.Visibility = Visibility.Visible;
    }

    private static readonly System.Windows.Media.Brush DropHighlight =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(48, 80, 140, 255));

    private static string? DroppedImageFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            ? files[0]
            : null;

    private void DetailImage_DragOver(object sender, DragEventArgs e)
    {
        var ok = Selected?.EgsId != null && DroppedImageFile(e) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DetailImageBox.Background = ok ? DropHighlight : System.Windows.Media.Brushes.Transparent;
        e.Handled = true;
    }

    private void DetailImage_DragLeave(object sender, DragEventArgs e) =>
        DetailImageBox.Background = System.Windows.Media.Brushes.Transparent;

    private void DetailImage_Drop(object sender, DragEventArgs e)
    {
        DetailImageBox.Background = System.Windows.Media.Brushes.Transparent;
        e.Handled = true;
        if (DroppedImageFile(e) is string file) ApplyCustomImage(file);
    }

    private void ReloadImage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.EgsId is not int egsId) return;
        DetailImage.Source = null;
        GameImageService.Forget(egsId);
        ShowImage(Selected);
    }

    /// <summary>この作品の画像を、自分で選んだ画像ファイルに差し替える（画像の表示がオフでも使える）</summary>
    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.EgsId is not int egsId)
        {
            MessageBox.Show("批評空間と紐づいていない作品には、画像を設定できません。", "画像の差し替え");
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "この作品の画像を選ぶ",
            Filter = "画像ファイル|*.jpg;*.jpeg;*.png;*.gif;*.webp|すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        ApplyCustomImage(dlg.FileName);
    }

    /// <summary>選んだ（またはドロップされた）画像ファイルを、選択中の作品の画像にする</summary>
    private void ApplyCustomImage(string file)
    {
        if (Selected?.EgsId is not int egsId) return;
        if (!GameImageService.SetCustom(egsId, file))
        {
            MessageBox.Show("画像として読み込めませんでした（JPEG / PNG / GIF / WebP の、10MB 以下のファイルを選んでください）。", "画像の差し替え");
            return;
        }
        StatusText.Text = "この作品の画像を差し替えました";
        ShowImage(Selected);
    }

    /// <summary>差し替えた画像をやめて、取得した画像に戻す</summary>
    private void ClearCustomImage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.EgsId is not int egsId) return;
        if (GameImageService.Custom(egsId) == null)
        {
            StatusText.Text = "差し替えた画像はありません";
            return;
        }
        GameImageService.ClearCustom(egsId);
        StatusText.Text = "差し替えをやめました";
        ShowImage(Selected);
    }

    private void ShowImages_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _cfg == null) return;
        _cfg.ShowImages = ShowImagesCheck.IsChecked == true;
        _cfg.Save();
        ShowImage(Selected);
    }

    private void ClearImages_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("保存した作品の画像をすべて削除しますか？\n（表示するときに、また取得します。自分で差し替えた画像は残します）", "画像の削除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DetailImage.Source = null;
        var failed = 0;
        foreach (var f in Directory.GetFiles(GameImageService.Dir))
        {
            if (f.EndsWith(".custom", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); } catch { failed++; }
        }
        StatusText.Text = failed == 0 ? "保存した画像を削除しました" : $"保存した画像を削除しました（使用中の {failed} 件を除く）";
        ShowImage(Selected);
    }

    // ================================================================
    // ログイン設定タブ
    // ================================================================

    private void InitLoginTab()
    {
        LoginUserBox.Text = CredentialStore.Load()?.User ?? "";
        UpdateLoginState();
    }

    private async void LoginSave_Click(object sender, RoutedEventArgs e)
    {
        if (_egsBusy)
        {
            LoginResultText.Text = "批評空間と通信中です。終わってからもう一度押してください。";
            return;
        }
        var user = LoginUserBox.Text.Trim();
        var pass = LoginPassBox.Password;
        if (user == "" || pass == "")
        {
            LoginResultText.Text = "ユーザーIDとパスワードを入力してください。";
            return;
        }

        // ログインの間は通信中にする（ログインの途中で同期などが始まって、ログイン状態が入れ替わらないように）
        if (!BeginEgsWork(lockDetail: false, quiet: true)) return;
        CredentialStore.Save(user, pass);
        LoginPassBox.Clear();
        LoginSaveButton.IsEnabled = false;
        LoginResultText.ClearValue(TextBlock.ForegroundProperty);
        LoginResultText.Text = "ログインを試しています…";
        try
        {
            await _egs.LoginAsync(user, pass);
            LoginResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3C, 0xB3, 0x71));
            LoginResultText.Text = "ログインに成功しました。次は「批評空間連携」タブで、作品を取り込んでください。";
        }
        catch (Exception ex)
        {
            LoginResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x53, 0x4B));
            LoginResultText.Text = "保存はしましたが、ログインに失敗しました：\n" + ex.Message;
        }
        finally
        {
            EndEgsWork();
            LoginSaveButton.IsEnabled = true;
            UpdateLoginState();
        }
    }

    private void LoginDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_egsBusy)
        {
            LoginResultText.Text = "批評空間と通信中です。終わってからもう一度押してください。";
            return;
        }
        if (MessageBox.Show("保存しているログイン情報を削除しますか？", "ログイン情報の削除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CredentialStore.Delete();
        _egs.Logout(); // ログイン状態も捨てる
        EgsClient.DeleteDebugFiles();
        LoginUserBox.Text = "";
        LoginPassBox.Clear();
        LoginResultText.Text = "ログイン情報を削除しました。";
        UpdateLoginState();
    }

    private void OpenLoginTab() => MainTabs.SelectedItem = LoginTab;

    // ================================================================
    // ゲーム起動設定タブ
    // ================================================================

    private void InitLaunchTab()
    {
        _loadingSettings = true;
        try
        {
            GameFolderList.ItemsSource = null;
            GameFolderList.ItemsSource = _cfg.GameFolders.ToList();
            ShortcutFolderList.ItemsSource = null;
            ShortcutFolderList.ItemsSource = _cfg.ShortcutFolders.ToList();
            ScanSteamCheck.IsChecked = _cfg.ScanSteam;
            UseShortcutsSettingCheck.IsChecked = _cfg.UseShortcuts;
            ScanStartMenuCheck.IsChecked = _cfg.ScanStartMenu;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void LaunchSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _cfg.ScanSteam = ScanSteamCheck.IsChecked == true;
        _cfg.UseShortcuts = UseShortcutsSettingCheck.IsChecked == true;
        _cfg.ScanStartMenu = ScanStartMenuCheck.IsChecked == true;
        _cfg.Save();
    }

    private string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    private void GameFolderAdd_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("ゲームを入れているフォルダを選んでください") is not string f) return;
        if (!_cfg.GameFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) _cfg.GameFolders.Add(f);
        _cfg.Save();
        InitLaunchTab();
    }

    private void GameFolderRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GameFolderList.SelectedItem is not string f) return;
        _cfg.GameFolders.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
        _cfg.Save();
        InitLaunchTab();
    }

    private void ShortcutFolderAdd_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("ショートカットを置いているフォルダを選んでください") is not string f) return;
        if (!_cfg.ShortcutFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) _cfg.ShortcutFolders.Add(f);
        _cfg.Save();
        InitLaunchTab();
    }

    private void ShortcutFolderRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutFolderList.SelectedItem is not string f) return;
        _cfg.ShortcutFolders.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
        _cfg.Save();
        InitLaunchTab();
    }
}
