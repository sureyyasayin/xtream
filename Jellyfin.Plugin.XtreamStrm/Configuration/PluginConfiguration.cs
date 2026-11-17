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

    /// <summary>Canli TV kanallarini Jellyfin Canli TV'ye ekle.</summary>
    public bool SyncLive { get; set; } = true;

    /// <summary>Alinacak canli TV kategorileri. Bos ise hicbir kanal alinmaz.</summary>
    public string[] LiveCategoryIds { get; set; } = Array.Empty<string>();

    /// <summary>Xtream yayin akisini (EPG) kullan.</summary>
    public bool LiveUseEpg { get; set; } = true;

    /// <summary>Kanal yayin bicimi: ts veya m3u8.</summary>
    public string LiveFormat { get; set; } = "ts";

    /// <summary>Ayni anda acilabilecek yayin sayisi (0 = sinirsiz).</summary>
    public int LiveTunerCount { get; set; }

    /// <summary>"Xtream Canli TV" kutusu (canli kanallar Xtream kategorilerine gore).</summary>
    public bool ShowLiveChannel { get; set; } = true;

    /// <summary>"Xtream Filmler" kutusu (filmler Xtream kategorilerine gore).</summary>
    public bool ShowVodChannel { get; set; } = true;

    /// <summary>"Xtream Diziler" kutusu (diziler Xtream kategorilerine gore).</summary>
    public bool ShowSeriesChannel { get; set; } = true;

    /// <summary>Xtream Filmler / Diziler kutusunda bir ogeye tiklaninca kutuphanedeki kartini ac.</summary>
    public bool OpenLibraryCard { get; set; } = true;

    /// <summary>Web istemcisine (LG webOS, tarayici) kumandayla kanal degistirme ekle.</summary>
    public bool LiveRemoteControl { get; set; } = true;

    /// <summary>Kanal degistirmede CH+/CH- disinda Yukari/Asagi oklari da kullanilsin.</summary>
    public bool LiveRemoteArrows { get; set; } = true;

    /// <summary>
    /// Xtream istekleri arasindaki bekleme (ms). Cogu saglayici dakikada ~30 istekten
    /// sonra gecici olarak 404 dondurur; 2500 ms dakikada 24 istek eder.
    /// </summary>
    public int RequestDelayMs { get; set; } = 2500;

    /// <summary>.strm dosyalarina Xtream kullanici adi/sifresi yerine eklentinin imzali adresini yaz.</summary>
    public bool HideCredentials { get; set; } = true;

    /// <summary>Oynaticilarin ulastigi Jellyfin adresi (bos ise JELLYFIN_PublishedServerUrl kullanilir).</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Yayinlari yonlendirmek yerine sunucu uzerinden aktar (sifre hicbir cihaza ulasmaz).</summary>
    public bool ProxyStreams { get; set; }

    /// <summary>Imzali adresler icin sunucuya ozel gizli anahtar (ilk senkronizasyonda uretilir).</summary>
    public string StreamSecret { get; set; } = string.Empty;

    /// <summary>Xtream listesinden kalkan icerigin .strm dosyalarini sil.</summary>
    public bool RemoveOrphans { get; set; } = true;

    /// <summary>"TR: ", "[4K] " gibi on ekleri isimden temizle.</summary>
    public bool StripPrefixes { get; set; } = true;

    /// <summary>Senkronizasyon bitince kutuphane taramasi baslat.</summary>
    public bool ScanLibraryAfterSync { get; set; } = true;
}
