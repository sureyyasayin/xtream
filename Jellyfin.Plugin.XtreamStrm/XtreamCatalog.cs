using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>"Xtream Filmler" kutusunun verisi: kategoriler Xtream sirasiyla.</summary>
public sealed class VodCatalog
{
    public List<VodCategory> Categories { get; set; } = new();
}

public sealed class VodCategory
{
    public string Name { get; set; } = string.Empty;

    public List<VodItem> Items { get; set; } = new();
}

public sealed class VodItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Ext { get; set; } = "mp4";

    public string? Icon { get; set; }

    public int? Year { get; set; }

    public float? Rating { get; set; }

    public string? Tmdb { get; set; }

    /// <summary>Kutuphanedeki .strm dosyasi (film karti acilsin diye); yoksa bos.</summary>
    public string? Path { get; set; }
}

/// <summary>"Xtream Diziler" kutusunun verisi: kategori -> dizi -> bolumler.</summary>
public sealed class SeriesCatalog
{
    public List<SeriesCategory> Categories { get; set; } = new();
}

public sealed class SeriesCategory
{
    public string Name { get; set; } = string.Empty;

    public List<CatalogSeries> Series { get; set; } = new();
}

public sealed class CatalogSeries
{
    public string Name { get; set; } = string.Empty;

    public string? Cover { get; set; }

    public string? Plot { get; set; }

    public int? Year { get; set; }

    public float? Rating { get; set; }

    public string? Tmdb { get; set; }

    /// <summary>Kutuphanedeki dizi klasoru ve dosya adi koku (dizi/bolum karti acilsin diye); yoksa bos.</summary>
    public string? Dir { get; set; }

    public string? Base { get; set; }

    public List<EpisodeItem> Episodes { get; set; } = new();

    /// <summary>Senkronizasyonun yazdigi bolum dosyasinin yolu.</summary>
    public string? EpisodePath(int season, int episode) =>
        Dir == null || Base == null
            ? null
            : System.IO.Path.Combine(Dir, $"Season {season:00}", $"{Base} S{season:00}E{episode:00}.strm");
}

public sealed class EpisodeItem
{
    public int S { get; set; }

    public int E { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Ext { get; set; } = "mp4";
}

/// <summary>Katalog dosyalarini yazar/okur; okunan katalog dosya degismedikce bellekte tutulur.</summary>
public static class CatalogStore
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (DateTime Stamp, object Value)> Cache = new(StringComparer.Ordinal);

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    public static T Load<T>(string path)
        where T : class, new()
    {
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        lock (Lock)
        {
            if (Cache.TryGetValue(path, out var c) && c.Stamp == stamp)
            {
                return (T)c.Value;
            }
        }

        T value;
        try
        {
            value = stamp == DateTime.MinValue ? new T() : JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch (JsonException)
        {
            value = new T();
        }

        lock (Lock)
        {
            Cache[path] = (stamp, value);
        }

        return value;
    }

    public static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    /// <summary>Kisa, kararli klasor kimligi.</summary>
    public static string Hash(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value)), 0, 8).ToLowerInvariant();
}
