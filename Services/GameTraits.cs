using System.IO;
using System.Text.Json;
using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>批評空間から取得した、作品の特徴（POV・ジャンル・総プレイ時間の中央値）</summary>
public class GameTraits
{
    /// <summary>保存形式の版（古い版の保存は、読み込まずに取得し直す）</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public int EgsId { get; set; }
    public string Genre { get; set; } = "";
    /// <summary>総プレイ時間の中央値（時間。不明なら null）</summary>
    public int? PlayHours { get; set; }
    /// <summary>POV：ランクAが一定数以上の、その作品の特徴的なもの（「ネガティブ」のうちシナリオに関わらないものは取得しない）</summary>
    public List<string> Pov { get; set; } = new();
    public DateTime FetchedAt { get; set; }
}

/// <summary>「次の1本」の絞り込み条件</summary>
public class TraitFilter
{
    public const int DefaultMinMedian = 80;

    /// <summary>批評空間の中央値の下限（null なら条件なし）</summary>
    public int? MinMedian { get; set; } = DefaultMinMedian;
    /// <summary>批評空間のデータ数（得点を付けた人数）の下限（null なら条件なし。中央値が少ない票で決まっている作品を除く）</summary>
    public int? MinDataCount { get; set; }
    /// <summary>総プレイ時間の中央値の上限（時間。null なら条件なし）</summary>
    public int? MaxPlayHours { get; set; }
    public bool IncludeUnplayed { get; set; } = true;
    public bool IncludePlaying { get; set; } = true;
    /// <summary>第一条件の POV（null なら条件なし）</summary>
    public string? Pov1 { get; set; }
    /// <summary>第二条件の POV（第一条件と「かつ」で組み合わせる。null なら条件なし）</summary>
    public string? Pov2 { get; set; }
    /// <summary>ジャンルの文字（含むもの。空なら条件なし）</summary>
    public string Genre { get; set; } = "";

    public bool NeedsTraits => Pov1 != null || Pov2 != null || MaxPlayHours != null || Genre.Trim().Length > 0;

    public TraitFilter Clone() => new()
    {
        MinMedian = MinMedian, MinDataCount = MinDataCount, MaxPlayHours = MaxPlayHours,
        IncludeUnplayed = IncludeUnplayed, IncludePlaying = IncludePlaying, Pov1 = Pov1, Pov2 = Pov2, Genre = Genre,
    };
}

/// <summary>絞り込みの判定（画面に触れないので、そのままテストできる）</summary>
public static class TraitMatcher
{
    /// <summary>
    /// 取得する POV か。批評空間の「ネガティブ」グループのPOVは、シナリオに関わる次の3つ以外は取得しない
    /// （152 ご都合主義な展開、106 中だるみ・テンポが悪い、528 個別ルートがダメ）
    /// </summary>
    public static readonly int[] KeptNegativePovIds = { 152, 106, 528 };

    /// <summary>絞り込みの対象にする作品か（状態・批評空間との連携・中央値・データ数）。特徴の条件は見ない</summary>
    public static bool IsBase(Game g, TraitFilter f)
    {
        if (g.EgsId == null) return false;
        var statusOk = (g.Status == GameStatus.Unplayed && f.IncludeUnplayed) || (g.Status == GameStatus.Playing && f.IncludePlaying);
        if (!statusOk) return false;
        if (g.Category == "発売前") return false; // 遊べない
        if (f.MinMedian is int m && (g.Median ?? -1) < m) return false;
        if (f.MinDataCount is int c && (g.EgsCount ?? 0) < c) return false;
        return true;
    }

    /// <summary>特徴の条件に合うか。特徴を取得していない作品は、特徴の条件があるとき合わない。skipPov1/2 は、その POV 条件を見ない（選択肢の数を数えるため）</summary>
    public static bool MatchesTraits(GameTraits? t, TraitFilter f, bool skipPov1 = false, bool skipPov2 = false)
    {
        var needs = (!skipPov1 && f.Pov1 != null) || (!skipPov2 && f.Pov2 != null) || f.MaxPlayHours != null || f.Genre.Trim().Length > 0;
        if (!needs) return true;
        if (t == null) return false;
        if (f.MaxPlayHours is int h && (t.PlayHours is not int ph || ph > h)) return false;
        var genre = f.Genre.Trim();
        if (genre.Length > 0 && !t.Genre.Contains(genre, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (!skipPov1 && f.Pov1 != null && !t.Pov.Contains(f.Pov1)) return false;
        if (!skipPov2 && f.Pov2 != null && !t.Pov.Contains(f.Pov2)) return false;
        return true;
    }

    public static List<Game> Select(IEnumerable<Game> games, IReadOnlyDictionary<int, GameTraits> traits, TraitFilter f) => games
        .Where(g => IsBase(g, f) && MatchesTraits(traits.GetValueOrDefault(g.EgsId!.Value), f))
        .OrderByDescending(g => g.Median ?? -1).ThenBy(g => g.Title, StringComparer.CurrentCulture)
        .ToList();

    /// <summary>
    /// 第一（second=false）／第二（second=true）の選択肢と、それを選んだときに残る作品数（多い順）。
    /// もう一方の POV 条件は反映するので、「選んだら0本だった」を避けられる。0本のものは出さない（選択中のものは残す）
    /// </summary>
    public static List<(string Name, int Count)> PovChoices(IEnumerable<Game> games, IReadOnlyDictionary<int, GameTraits> traits, TraitFilter f, bool second)
    {
        var rows = games
            .Where(g => IsBase(g, f))
            .Select(g => traits.GetValueOrDefault(g.EgsId!.Value))
            .Where(t => t != null && MatchesTraits(t, f, skipPov1: !second, skipPov2: second))
            .SelectMany(t => t!.Pov.Distinct())
            .GroupBy(n => n)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .ToList();
        var selected = second ? f.Pov2 : f.Pov1;
        if (selected != null && rows.All(r => r.Name != selected)) rows.Add((selected, 0));
        return rows.OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToList();
    }
}

/// <summary>取得した特徴の保存場所（egs_traits.json。消えても取得し直せる）</summary>
public static class TraitStore
{
    /// <summary>この日数より古い特徴は、更新の対象にする</summary>
    public const int StaleDays = 60;

    public static string DefaultPath => Path.Combine(AppPaths.Dir, "egs_traits.json");

    public static Dictionary<int, GameTraits> Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new();
            var list = JsonSerializer.Deserialize<List<GameTraits>>(File.ReadAllText(path)) ?? new();
            return list.Where(t => t.EgsId > 0 && t.Version == GameTraits.CurrentVersion)
                .GroupBy(t => t.EgsId).ToDictionary(g => g.Key, g => g.Last());
        }
        catch (Exception ex)
        {
            Log.Warn("作品の特徴の保存ファイルを読めませんでした（取得し直せます）", ex);
            return new();
        }
    }

    public static void Save(IEnumerable<GameTraits> traits, string? path = null)
    {
        path ??= DefaultPath;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(traits.OrderBy(t => t.EgsId).ToList()));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>取得・更新が必要な作品（未取得、または古い）の批評空間 ID</summary>
    public static List<int> NeedFetch(IEnumerable<Game> baseGames, IReadOnlyDictionary<int, GameTraits> traits, DateTime now) => baseGames
        .Select(g => g.EgsId!.Value).Distinct()
        .Where(id => !traits.TryGetValue(id, out var t) || (now - t.FetchedAt).TotalDays > StaleDays)
        .ToList();
}
