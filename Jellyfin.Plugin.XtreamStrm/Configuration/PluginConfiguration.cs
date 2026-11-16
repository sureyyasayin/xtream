using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.XtreamStrm.Configuration;

/// <summary>Eklenti ayarlari (Jellyfin panelinden duzenlenir).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Xtream sunucu adresi, ornek: http://sunucu.com:8080</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Film .strm dosyalarinin yazilacagi klasor (container icindeki yol).</summary>
    public string MoviesPath { get; set; } = "/data/movies";

    /// <summary>Dizi .strm dosyalarinin yazilacagi klasor (container icindeki yol).</summary>
    public string SeriesPath { get; set; } = "/data/tvshows";

    public bool SyncMovies { get; set; } = true;

    public bool SyncSeries { get; set; } = true;

    /// <summary>Senkronize edilecek film kategorileri. Bos ise hicbir film alinmaz.</summary>
    public string[] MovieCategoryIds { get; set; } = Array.Empty<string>();

    /// <summary>Senkronize edilecek dizi kategorileri. Bos ise hicbir dizi alinmaz.</summary>
    public string[] SeriesCategoryIds { get; set; } = Array.Empty<string>();

    /// <summary>Xtream listesinden kalkan icerigin .strm dosyalarini sil.</summary>
    public bool RemoveOrphans { get; set; } = true;

    /// <summary>"TR: ", "[4K] " gibi on ekleri isimden temizle.</summary>
    public bool StripPrefixes { get; set; } = true;

    /// <summary>Senkronizasyon bitince kutuphane taramasi baslat.</summary>
    public bool ScanLibraryAfterSync { get; set; } = true;
}
