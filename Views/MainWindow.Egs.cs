using System.Windows;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>メイン画面のうち、詳細の編集・保存と、批評空間との連動（反映・同期・一括取り込み）を担当する部分</summary>
public partial class MainWindow
{
    // ================================================================
    // 詳細の編集・保存
    // ================================================================

    private void PlayedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDetail) return;
        var played = PlayedCheck.IsChecked == true;
        StatusBox.IsEnabled = !played;
        if (played && ClearDatePicker.SelectedDate == null)
            ClearDatePicker.SelectedDate = DateTime.Today; // クリア日の初期値は今日
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null) return;

        // 任意評価基準：項目別の採点にエラーがあれば、赤字で知らせて保存しない（批評空間にも送らない）
        var scoreCheck = CheckScoreItemsForSave();
        if (scoreCheck == null) return;

        // 得点のチェック（空欄は「未入力」）。項目別の採点があるときは、その合計が得点になる
        int? score = null;
        var scoreText = scoreCheck.AnyEntered ? scoreCheck.Total!.Value.ToString() : ScoreBox.Text.Trim();

        // 得点だけ入れてあった作品に、初めて項目別の点数を入れるときは、得点が合計に置き換わることを確かめる
        if (scoreCheck.AnyEntered && g.MyScore != null && !ScoreItemRules.HasItemScores(g.Review) && g.MyScore != scoreCheck.Total &&
            MessageBox.Show($"「{g.Title}」の得点（{g.MyScore}）は、項目の合計（{scoreCheck.Total}）に置き換わります。\n\n" +
                            "批評空間に反映すると、批評空間側の得点も変わります。\n続けますか？\n（［いいえ］で保存を中止します）",
                "得点の置き換え", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        if (scoreText != "")
        {
            if (!int.TryParse(scoreText, out var sv) || sv < 0 || sv > 100)
            {
                MessageBox.Show("得点は 0～100 の整数で入力してください。", "入力エラー");
                return;
            }
            score = sv;
        }

        // 得点を消す操作は、批評空間側の得点も消えるので、先に確かめる（誤って空欄にしたときに備えて）
        if (g.EgsId != null && g.MyScore != null && score == null &&
            MessageBox.Show($"「{g.Title}」の得点（{g.MyScore}）を空欄にします。\n\n" +
                            "批評空間に反映すると、批評空間側の得点も消えます。\n続けますか？\n（［いいえ］で保存を中止します）",
                "得点を空欄にする", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // 購入額（円。任意。カンマ・円記号は無視する。空欄は「未入力」）
        int? price = null;
        var priceText = PriceBox.Text.Replace(",", "").Replace("，", "").Replace("円", "").Replace("¥", "").Replace("￥", "").Trim();
        if (priceText != "")
        {
            if (!int.TryParse(priceText, out var pv) || pv < 0 || pv > 99_999_999)
            {
                MessageBox.Show("購入額は 0～99,999,999 の整数（円）で入力してください。", "入力エラー");
                return;
            }
            price = pv;
        }

        // 得点登録フォームのその他の項目
        var review = ReadReviewFromUi(g);
        if (review == null) return;
        ApplyScoreItemsToReview(review, scoreCheck);

        var before = SavedState.Of(g);
        g.Review = review;
        g.MyScore = score;
        g.Price = price; // ツールだけに保存（批評空間には送らない）
        g.EgsRegistered = RegisteredPicker.SelectedDate?.Date;

        // 起動するもの（TargetGrid）は、変更したその場で保存しているので、ここでは扱わない
        g.ClearDate = ClearDatePicker.SelectedDate?.Date;
        g.Status = PlayedCheck.IsChecked == true
            ? GameStatus.Played
            : StatusBox.SelectedIndex switch
            {
                1 => GameStatus.Playing,
                2 => GameStatus.Dropped,
                _ => GameStatus.Unplayed,
            };
        if (g.EgsId != null) g.PendingReview.UnionWith(PushEngine.ChangedKeys(before, g)); // 反映するまで「未反映」として覚えておく
        _db.Update(g);
        _detailDirty = false;
        Reload();
        StatusText.Text = "保存しました";

        if (g.EgsId == null || g.PendingReview.Count == 0) return;
        if (PushOnSaveCheck.IsChecked != true)
        {
            StatusText.Text = "保存しました（批評空間には未反映です。次の同期で反映します）";
            return;
        }
        if (!BeginEgsWork(lockDetail: false, quiet: true))
        {
            StatusText.Text = "保存しました（批評空間と通信中のため、反映は次の同期で行います）";
            return;
        }
        try
        {
            await PushToEgsAsync(g.Id);
        }
        finally
        {
            EndEgsWork();
        }
    }

    /// <summary>
    /// 未反映の変更を捨てる。批評空間が反映を受け付けなかったとき（例：得点が残っていてプレイ済を外せない）や、
    /// 批評空間のサイトで直接変更したときに、ツールの古い内容で批評空間の内容が上書きされ続けないようにする
    /// </summary>
    private void DiscardPending_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Game selected) return;
        var g = _db.Get(selected.Id);
        if (g == null || g.PendingReview.Count == 0) return;

        var labels = string.Join("、", g.PendingReview.Select(PendingLabel));
        if (MessageBox.Show($"「{g.Title}」の未反映の変更（{labels}）を破棄します。\n\n" +
                            "ツールで変更したこれらの内容は、批評空間に送られなくなります。\n" +
                            "次に「⇅ 批評空間と同期」を押すと、批評空間の内容がツールに取り込まれます。\n\n続けますか？",
                "未反映の変更を破棄", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        g.PendingReview.Clear();
        _db.UpdateSyncState(g);
        _detailDirty = false;
        Reload();
        StatusText.Text = $"未反映の変更を破棄しました：{g.Title}（「⇅ 批評空間と同期」で、批評空間の内容を取り込めます）";
    }

    private void OpenEgsPage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.EgsId is int id) OpenUrl(_egs.GamePageUrl(id));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null) return;
        var ok = MessageBox.Show($"「{g.Title}」を一覧から削除しますか？\n（プレイ時間の記録も消えます。批評空間側は変わりません）",
            "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        _db.Delete(g.Id);
        Reload();
    }

    // ================================================================
    // 批評空間との連動
    // ================================================================

    /// <summary>
    /// ツール → 批評空間：まだ反映していない項目（PendingReview）だけを送り、読み直して確認する。
    ///  - 送る直前と確認の後に、作品を DB から読み直す（通信中に画面で保存された内容を、古い内容で上書きしないため）
    ///  - 批評空間で実際に変わったことを確かめた項目だけを「反映済み」にする
    /// </summary>
    private async Task<bool> PushToEgsAsync(long gameId)
    {
        var g = _db.Get(gameId);
        if (g == null || PushEngine.Plan(g) is not { } plan) return true;

        try
        {
            StatusText.Text = $"批評空間に反映中：{g.Title}";
            await _egs.UpdateReviewAsync(plan.EgsId, plan.Played, plan.ClearDate, plan.Score, plan.FormValues(_cfg), plan.ClearScore);
        }
        catch (Exception ex)
        {
            Log.Warn("批評空間への反映に失敗", ex);
            StatusText.Text = "批評空間への反映に失敗しました（未反映の項目は、次の同期でまた反映します）";
            var r = MessageBox.Show(this,
                $"批評空間への反映に失敗しました。\n\n{ex.Message}\n\n得点登録ページをブラウザで開きますか？（手動で登録できます）",
                "反映エラー", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) OpenUrl(_egs.ReviewFormUrl(plan.EgsId));
            return false;
        }

        // ---- 批評空間を読み直して、反映されたか確かめる ----
        EgsUserRecord? rec = null;
        Review? remote = null;
        var checkedOk = false;
        try
        {
            var cred = CredentialStore.Load();
            if (cred != null)
            {
                rec = (await _egs.GetUserRecordsAsync(cred.Value.User)).FirstOrDefault(x => x.GameId == plan.EgsId);
                if (plan.ReviewKeys.Count > 0)
                    remote = (await _egs.GetReviewsAsync(cred.Value.User, plan.EgsId)).GetValueOrDefault(plan.EgsId);
                checkedOk = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("反映の確認に失敗", ex);
        }

        Log.Info($"批評空間へ反映後の確認 game={plan.EgsId} 送信played={plan.SentPlayed} 読み直しplayed={rec?.Played} 送信日={plan.SentClear:yyyy-MM-dd} 読み直し日={rec?.ClearDate:yyyy-MM-dd} 確認できた={checkedOk}");
        var fresh = _db.Get(gameId);
        if (fresh == null) return true; // 通信中に一覧から削除された

        var done = PushEngine.Complete(plan, checkedOk, rec, remote, fresh);
        _db.UpdateSyncState(fresh); // 同期の状態だけを書く（画面で保存した内容には触れない）
        Reload();

        var notYet = plan.Keys.Except(done).ToList();
        StatusText.Text = !checkedOk
            ? $"送信しましたが、反映を確認できませんでした：{g.Title}（次の同期で確認します）"
            : notYet.Count == 0
                ? $"批評空間に反映しました：{g.Title}"
                : $"送信しましたが、反映を確認できなかった項目があります：{string.Join("、", notYet.Select(PendingLabel))}（次の同期でまた反映します）";
        return true;
    }

    /// <summary>未反映の項目の、画面での名前</summary>
    private static string PendingLabel(string key) => key switch
    {
        "played" => "プレイ済",
        "cleardate" => "終了日",
        "score" => "得点",
        _ => Review.Keys.FirstOrDefault(k => k.Key == key).Label ?? key,
    };

    /// <summary>この時間より前に同期していたら、続けて同期するときに確認する</summary>
    private static readonly TimeSpan SyncRepeatWarning = TimeSpan.FromMinutes(5);

    private System.Windows.Threading.DispatcherTimer? _syncCooldownTimer;

    /// <summary>続けて同期できるようになるまでの残り時間（押してよければ null）</summary>
    private TimeSpan? SyncCooldownLeft() =>
        SyncThrottle.Remaining(_cfg.LastSyncAttemptAt, _cfg.SyncFailureCount, DateTime.Now);

    /// <summary>同期ボタンに、前回の同期の日時と、制限中なら残り時間を出す</summary>
    private void UpdateSyncTooltip()
    {
        var tip = _cfg.LastSyncAt is DateTime t ? $"前回の同期：{t:M/d HH:mm}" : "まだ同期していません";
        if (SyncCooldownLeft() is TimeSpan left)
        {
            tip += $"\n続けての同期は、あと {Math.Ceiling(left.TotalSeconds):0} 秒たってから押せます";
            _syncCooldownTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _syncCooldownTimer.Tick -= SyncCooldownTick;
            _syncCooldownTimer.Tick += SyncCooldownTick;
            _syncCooldownTimer.Start();
        }
        else
        {
            _syncCooldownTimer?.Stop();
        }
        SyncButtonTop.ToolTip = tip;
        SyncButtonTab.ToolTip = tip;
    }

    private void SyncCooldownTick(object? sender, EventArgs e) => UpdateSyncTooltip();

    /// <summary>同期を始めた記録を残す（結果によらず、続けて押せない時間の起点になる）</summary>
    private void RecordSyncAttempt()
    {
        _cfg.LastSyncAttemptAt = DateTime.Now;
        SaveSyncState();
        UpdateSyncTooltip();
    }

    /// <summary>同期の結果を記録する。失敗が続くと、次に押せるまでの時間が延びる</summary>
    private void RecordSyncResult(bool success)
    {
        _cfg.SyncFailureCount = success ? 0 : Math.Min(_cfg.SyncFailureCount + 1, 10);
        SaveSyncState();
        UpdateSyncTooltip();
    }

    private void SaveSyncState()
    {
        try { _cfg.Save(); }
        catch (Exception ex) { Log.Warn("同期の記録を保存できませんでした", ex); } // 保存できなくても同期は続ける
    }

    /// <summary>批評空間 → ツール：自分の記録を読み込んで突き合わせる</summary>
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var cred = CredentialStore.Load();
        if (cred == null)
        {
            MessageBox.Show("先に「ログイン設定」タブで、批評空間のIDとパスワードを登録してください。");
            return;
        }
        if (SyncCooldownLeft() is TimeSpan left)
        {
            StatusText.Text = $"続けての同期は、あと {Math.Ceiling(left.TotalSeconds):0} 秒たってから押せます（批評空間への負荷を避けるため）";
            UpdateSyncTooltip();
            return;
        }
        if (_cfg.LastSyncAt is DateTime last && DateTime.Now - last is { } ago && ago >= TimeSpan.Zero && ago < SyncRepeatWarning)
        {
            var minutes = Math.Max(1, (int)ago.TotalMinutes);
            var ans = MessageBox.Show($"前回の同期から {minutes} 分しかたっていません。\n批評空間に負荷がかかるため、続けて何度も押さないでください。\n\nもう一度同期しますか？",
                "同期の確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes) return;
        }
        if (!BeginEgsWork(lockDetail: true)) return;
        try
        {
            RecordSyncAttempt();
            await SyncAsync(cred.Value.User);
        }
        finally
        {
            EndEgsWork();
        }
    }

    private async Task SyncAsync(string uid)
    {
        List<EgsUserRecord> records;
        try
        {
            StatusText.Text = "批評空間から読み込み中…";
            records = await _egs.GetUserRecordsAsync(uid);
        }
        catch (Exception ex)
        {
            Log.Warn("批評空間からの読み込みに失敗（同期）", ex);
            StatusText.Text = "読み込みに失敗しました";
            RecordSyncResult(success: false);
            MessageBox.Show(ex.Message, "同期エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cfg.LastSyncAt = DateTime.Now;
        RecordSyncResult(success: true);

        if (SyncEngine.LooksLikeLostRecords(_db.GetAll(), records.Count) &&
            MessageBox.Show("批評空間の記録が0件でした。取得に失敗している可能性があります。\n\n" +
                            "このまま同期すると、プレイ済の作品がすべて「批評空間に未登録」として扱われます。\n続けますか？",
                "同期の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            StatusText.Text = "同期を中止しました（批評空間の記録が0件でした）";
            return;
        }

        var byId = records.GroupBy(r => r.GameId).ToDictionary(x => x.Key, x => x.First());

        // 得点登録フォームのその他の項目（所持・感想など）
        Dictionary<int, Review>? reviews = null;
        try
        {
            StatusText.Text = "感想などを読み込み中…";
            reviews = await _egs.GetReviewsAsync(uid);
        }
        catch (Exception ex)
        {
            Log.Warn("感想などの読み込みに失敗", ex);
            MessageBox.Show("感想などの項目を読み込めませんでした（プレイ済・得点などの同期は続けます）。\n\n" + ex.Message,
                "同期", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 中央値・平均値・データ数・発売日を最新にする（失敗しても同期は続ける）
        var infos = new Dictionary<int, EgsGameInfo>();
        try
        {
            StatusText.Text = "作品の情報（中央値など）を更新中…";
            var ids = _db.GetAll().Where(x => x.EgsId != null).Select(x => x.EgsId!.Value).ToList();
            foreach (var info in await _egs.GetGameInfosAsync(ids)) infos[info.Id] = info;
        }
        catch (Exception ex)
        {
            Log.Warn("作品の情報の更新に失敗", ex);
        }

        // 読み込みが終わってから最新の内容を読む（読み込み中に保存された内容も含める）
        var stats = new SyncStats();
        var resolver = new DialogConflictResolver(this);
        var scoredBefore = SnapshotScoredGames(); // 任意評価基準：得点が変わって食い違った作品を、あとで知らせる
        var synced = _db.GetAll().Where(x => x.EgsId != null).ToList();
        foreach (var g in synced)
        {
            var egsId = g.EgsId!.Value;
            byId.TryGetValue(egsId, out var rec);
            infos.TryGetValue(egsId, out var info);
            Review? remote = null;
            reviews?.TryGetValue(egsId, out remote);
            SyncEngine.ApplyRecord(g, rec, info, remote, resolver, stats);
        }
        // 食い違いの確認（画面）が全部すんでから、まとめて1回で書き込む。起動設定など、同期と関係のない項目は書き換えない
        _db.UpdateFromSync(synced);
        _detailDirty = false; // 同期中は詳細欄を編集できないので、読み直してよい
        Reload();
        StatusText.Text = $"同期しました（プレイ済にした作品：{stats.Imported}件、中断にした作品：{stats.Dropped}件、得点を取り込んだ作品：{stats.ScoreImported}件）";
        ReportScoreMismatches(scoredBefore);

        // ---- ツールの内容を批評空間に反映する ----
        var toPush = _db.GetAll().Where(x => x.EgsId != null && x.PendingReview.Count > 0).ToList();
        if (toPush.Count == 0) return;
        var ok = MessageBox.Show(
            $"ツール側の内容を批評空間に反映する作品が {toPush.Count} 件あります。\n\n" +
            string.Join("\n", toPush.Take(10).Select(x => "・" + x.Title)) +
            (toPush.Count > 10 ? "\n…" : "") + "\n\n反映しますか？\n（［いいえ］の場合は「未反映」のまま残し、次の同期でまた確認します）",
            "批評空間へ反映", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        foreach (var pushGame in toPush)
        {
            if (!await PushToEgsAsync(pushGame.Id)) break; // 失敗したらそこで止める
        }
    }

    /// <summary>批評空間 → ツール：自分が登録している作品をまとめて一覧に追加する</summary>
    private async void ImportAll_Click(object sender, RoutedEventArgs e)
    {
        var cred = CredentialStore.Load();
        if (cred == null)
        {
            MessageBox.Show("先に「ログイン設定」タブで、批評空間のIDとパスワードを登録してください。");
            return;
        }

        // 同期と共通の制限（重い問い合わせを続けて送らない）
        if (SyncCooldownLeft() is TimeSpan left)
        {
            StatusText.Text = $"続けての取り込みは、あと {Math.Ceiling(left.TotalSeconds):0} 秒たってから行えます（批評空間への負荷を避けるため）";
            UpdateSyncTooltip();
            return;
        }

        var dlg = new ImportWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var scope = dlg.Scope;
        if (!BeginEgsWork(lockDetail: true)) return;
        try
        {
            RecordSyncAttempt();

            List<EgsMyGame> myGames;
            try
            {
                StatusText.Text = "批評空間から読み込み中…";
                myGames = await _egs.GetMyGamesAsync(cred.Value.User, scope);
            }
            catch (Exception ex)
            {
                Log.Warn("批評空間からの読み込みに失敗（一括取り込み）", ex);
                StatusText.Text = "読み込みに失敗しました";
                RecordSyncResult(success: false);
                MessageBox.Show(ex.Message, "取り込みエラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RecordSyncResult(success: true);

            // 所持・感想など（読めなくても取り込みは続ける）
            var reviews = new Dictionary<int, Review>();
            try
            {
                StatusText.Text = "感想などを読み込み中…";
                reviews = await _egs.GetReviewsAsync(cred.Value.User);
            }
            catch (Exception ex)
            {
                Log.Warn("感想などの読み込みに失敗", ex);
            }

            int added = 0, skipped = 0, scoreFilled = 0;
            var existing = _db.GetAll()
                .Where(x => x.EgsId != null)
                .GroupBy(x => x.EgsId!.Value)
                .ToDictionary(x => x.Key, x => x.First());

            var updated = new List<Game>();
            var newGames = new List<Game>();
            foreach (var item in myGames)
            {
                reviews.TryGetValue(item.Game.Id, out var remote);
                if (existing.TryGetValue(item.Game.Id, out var old))
                {
                    if (SyncEngine.ApplyImportToExisting(old, item, remote)) scoreFilled++;
                    updated.Add(old);
                    skipped++;
                    continue;
                }

                newGames.Add(SyncEngine.NewGameFromImport(item, remote));
                added++;
            }
            _db.SaveImport(updated, newGames);

            Reload();
            StatusText.Text = $"一括取り込み完了：{added} 件追加、得点を取り込んだ作品 {scoreFilled} 件";
            MessageBox.Show(
                $"{added} 件を追加しました。\n" +
                $"すでに一覧にあった {skipped} 件のうち、得点が未入力だった {scoreFilled} 件に批評空間の得点を入れました。\n" +
                "（プレイ済・クリア日などの状態をそろえたいときは「批評空間と同期」を押してください）\n\n" +
                "起動するには、各作品の実行ファイルを右側の詳細欄で設定してください。",
                "一括取り込み");
        }
        finally
        {
            EndEgsWork();
        }
    }

}
