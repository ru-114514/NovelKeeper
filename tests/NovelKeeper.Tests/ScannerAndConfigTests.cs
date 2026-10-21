using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>実行ファイルの自動検出のテスト（一時フォルダにゲームのフォルダを作って確かめる）</summary>
public sealed class GameScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NovelKeeperScan_" + Guid.NewGuid().ToString("N"));

    public GameScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* 一時ファイルなので残っても害はない */ }
    }

    private string Touch(string relative, int size = 100)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]); // 中身は空でよい（製品名などは読めない exe として扱われる）
        return path;
    }

    // ---------------- タイトルの照合 ----------------

    [Theory]
    [InlineData("AMBITIOUS MISSION", "ambitious mission", 1.0)]    // 大文字・小文字
    [InlineData("ＡＢＣ　ゲーム", "abcゲーム", 1.0)]                   // 全角・半角、空白
    [InlineData("作品名：副題", "作品名", 0.7)]                       // 副題の有無（含む）は 0.7 以上
    public void Similarity_IgnoresCaseWidthAndSymbols(string a, string b, double atLeast) =>
        Assert.True(GameScanner.Similarity(a, b) >= atLeast);

    [Fact]
    public void Similarity_UnrelatedNamesAreLow_AndEmptyIsZero()
    {
        Assert.True(GameScanner.Similarity("まったく違う作品", "ABCDEFG") < 0.3);
        Assert.Equal(0, GameScanner.Similarity("", "abc"));
        Assert.Equal(1, GameScanner.Similarity("同じ", "同じ"));
    }

    // ---------------- 起動する exe の選び方 ----------------

    [Fact]
    public void RankExes_ExcludesUninstallersAndSetupTools()
    {
        var dir = Path.Combine(_root, "G");
        Touch(@"G\game.exe", 5000);
        Touch(@"G\unins000.exe", 9000);
        Touch(@"G\setup.exe", 9000);
        Touch(@"G\config.exe", 9000);

        var ranked = GameScanner.RankExes(dir, [], maxDepth: 1);

        Assert.Equal(["game.exe"], ranked.Select(c => Path.GetFileName(c.Path)));
    }

    [Fact]
    public void RankExes_PrefersUnityGames_OverBiggerOtherExes()
    {
        var dir = Path.Combine(_root, "U");
        Touch(@"U\launcher.exe", 100_000);
        var unity = Touch(@"U\Game.exe", 5000);
        Directory.CreateDirectory(Path.Combine(dir, "Game_Data"));

        var ranked = GameScanner.RankExes(dir, [], maxDepth: 1);

        Assert.Equal(unity, ranked[0].Path);
        Assert.Contains("Unity", ranked[0].Reason);
    }

    [Fact]
    public void RankExes_PrefersShortcutTarget_Above_Everything()
    {
        var dir = Path.Combine(_root, "S");
        var other = Touch(@"S\main.exe", 5000);
        Directory.CreateDirectory(Path.Combine(dir, "main_Data"));
        var chosenByMaker = Touch(@"S\play.exe", 100);

        var ranked = GameScanner.RankExes(dir, [chosenByMaker], maxDepth: 1);

        Assert.Equal(chosenByMaker, ranked[0].Path);
        Assert.Equal("ショートカット", ranked[0].Reason);
        Assert.Contains(ranked, c => c.Path == other);
    }

    [Fact]
    public void RankExes_PrefersExeWhoseFolderLevelIsShallower()
    {
        var dir = Path.Combine(_root, "D");
        Touch(@"D\a.exe", 1000);
        Touch(@"D\sub\deep\b.exe", 1000);

        var ranked = GameScanner.RankExes(dir, [], maxDepth: 2);

        Assert.Equal("a.exe", Path.GetFileName(ranked[0].Path));
        Assert.Equal(2, ranked.Count);
    }

    // ---------------- フォルダのまとめ検出 ----------------

    [Fact]
    public void ScanAll_FindsGameFolders_DirectlyAndUnderABrandFolder()
    {
        Touch(@"GameA\a.exe", 3000);
        Touch(@"Brand\GameB\b.exe", 3000);
        Touch(@"Brand\readme.txt");        // exe のないフォルダは対象外
        Touch(@"Empty\note.txt");

        var found = GameScanner.ScanAll([_root], scanSteam: false, [], scanStartMenu: false);

        Assert.Equal(["GameA", "GameB"], found.Select(g => Path.GetFileName(g.InstallDir)).OrderBy(x => x));
        Assert.All(found, g => Assert.Equal("フォルダ", g.Source));
        Assert.All(found, g => Assert.True(g.IsExeCertain)); // exe が1つだけなら、特定できている
    }

    [Fact]
    public void ScanAll_IgnoresMissingFolders_AndNamesComeFromTheFolder()
    {
        Touch(@"Some Game\game.exe", 3000);

        var found = GameScanner.ScanAll([Path.Combine(_root, "nothing"), _root], scanSteam: false, [], scanStartMenu: false);

        var game = Assert.Single(found);
        Assert.Equal("Some Game", game.DisplayName);
        Assert.Contains("Some Game", game.Names);
    }

    [Fact]
    public void ScanAll_GameWithSeveralLikelyExes_NeedsConfirmation()
    {
        Touch(@"Multi\a.exe", 3000);
        Touch(@"Multi\b.exe", 3000);

        var game = Assert.Single(GameScanner.ScanAll([_root], scanSteam: false, [], scanStartMenu: false));

        Assert.False(game.IsExeCertain);
        Assert.Equal(2, game.Candidates.Count);
    }
}

/// <summary>設定ファイルの読み書きのテスト</summary>
public sealed class EgsConfigFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NovelKeeperCfg_" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "egs_config.json");

    public EgsConfigFileTests()
    {
        Directory.CreateDirectory(_dir);
        EgsConfig.ResetNotice = null;
    }

    public void Dispose()
    {
        EgsConfig.ResetNotice = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 一時ファイルなので残っても害はない */ }
    }

    [Fact]
    public void Load_CreatesTheFileWithDefaults_WhenMissing()
    {
        var cfg = EgsConfig.Load(File_);

        Assert.True(File.Exists(File_));
        Assert.Equal("Dark", cfg.Theme);
        Assert.Null(EgsConfig.ResetNotice);
    }

    [Fact]
    public void SavedChanges_SurviveReload_IncludingJapaneseText()
    {
        var cfg = EgsConfig.Load(File_);
        cfg.Theme = "Light";
        cfg.GameFolders.Add(@"D:\ノベルゲーム");
        cfg.ReviewFields["memo"] = "data[x]";
        cfg.Save();

        var again = EgsConfig.Load(File_);

        Assert.Equal("Light", again.Theme);
        Assert.Equal([@"D:\ノベルゲーム"], again.GameFolders);
        Assert.Equal("data[x]", again.ReviewFormField("memo"));
        Assert.Contains("ノベルゲーム", File.ReadAllText(File_)); // 日本語がそのまま書かれている
    }

    [Fact]
    public void Save_LeavesNoTemporaryFile()
    {
        EgsConfig.Load(File_).Save();
        Assert.Equal([File_], Directory.GetFiles(_dir));
    }

    [Fact]
    public void BrokenFile_IsBackedUp_AndReplacedWithDefaults_WithANotice()
    {
        File.WriteAllText(File_, "{ これは壊れたJSON");

        var cfg = EgsConfig.Load(File_);

        Assert.Equal("Dark", cfg.Theme);
        Assert.NotNull(EgsConfig.ResetNotice);
        var backup = Assert.Single(Directory.GetFiles(_dir, "*.broken-*"));
        Assert.Equal("{ これは壊れたJSON", File.ReadAllText(backup)); // 元の内容が控えに残っている
        Assert.Equal("Dark", EgsConfig.Load(File_).Theme);          // 作り直したファイルは正常に読める
    }

    [Fact]
    public void OldFileWithoutNewSettings_LoadsAndGetsThemWrittenBack()
    {
        File.WriteAllText(File_, """{ "Theme": "System", "PlayedField": "data[play]" }""");

        var cfg = EgsConfig.Load(File_);

        Assert.Equal("System", cfg.Theme);
        Assert.Equal("data[hitokoto]", cfg.ReviewFormField("hitokoto"));
        Assert.Contains("ReviewFields", File.ReadAllText(File_)); // 新しい項目が追記されている
    }
}
