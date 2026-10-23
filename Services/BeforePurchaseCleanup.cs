using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>
/// 購入前の作品の整理：批評空間では購入予定を「ー」（と購入前得点を空）にして登録すると、空になった記録が消える。
/// その送信内容を作る処理と、反映できたかの判定。画面にも通信にも触れないので、そのままテストできる。
/// </summary>
public static class BeforePurchaseCleanup
{
    /// <summary>整理の対象にできる作品（いま「購入前」と判定される作品）</summary>
    public static List<Game> Candidates(IEnumerable<Game> games) => games.Where(g => g.IsBeforePurchase).ToList();

    /// <summary>
    /// 批評空間に残るツール以外の記録（得点・感想）が、手元にあるか。
    /// 購入予定を「ー」にしても、これらは消えない（記録が消えるのは、すべて空のとき）ため、一覧で目印にする
    /// </summary>
    public static bool HasOwnContent(Game g) =>
        g.MyScore != null || !string.IsNullOrWhiteSpace(g.Review.Hitokoto) || !string.IsNullOrWhiteSpace(g.Review.Memo);

    /// <summary>
    /// 購入予定を「ー」・購入前得点を空にして、批評空間に送る項目（未反映）にする。
    /// 手元がすでに空でも、批評空間側に残っているかもしれないので、必ず送る
    /// </summary>
    public static void Prepare(Game g, bool eraseContent = false)
    {
        g.Review.PurchaseWill = "";
        g.Review.BeforeScore = null;
        g.PendingReview.Add("purchase_will");
        g.PendingReview.Add("before_tokuten");

        // 得点・感想も空にする（選んだときだけ）。書いた内容が失われる代わりに、批評空間の記録が消える
        if (!eraseContent) return;
        g.MyScore = null;
        g.Review.Hitokoto = "";
        g.Review.Memo = "";
        g.PendingReview.Add("score");
        g.PendingReview.Add("hitokoto");
        g.PendingReview.Add("memo");
    }

    /// <summary>送った結果、批評空間への反映を確認できたか（確認できた項目は、未反映から外れている）</summary>
    public static bool IsReflected(Game fresh, bool eraseContent = false) =>
        !fresh.PendingReview.Contains("purchase_will") && !fresh.PendingReview.Contains("before_tokuten") &&
        (!eraseContent || !(fresh.PendingReview.Contains("score") || fresh.PendingReview.Contains("hitokoto") || fresh.PendingReview.Contains("memo")));
}
