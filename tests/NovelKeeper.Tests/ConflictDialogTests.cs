using ErogeLauncher.Views;

namespace NovelKeeper.Tests;

public class ConflictDialogTests
{
    /// <summary>画面の部品を、画面を出さずに作れるか（組み立ての誤りで、同期の途中で落ちないことの確認）</summary>
    [Fact]
    public void ChoiceWindow_CanBeBuilt()
    {
        Exception? error = null;
        bool remember = true;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new ChoiceWindow("タイトル", "本文", "ツール", "批評空間", "以降も同じ");
                remember = window.Remember;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.False(remember);
    }
}
