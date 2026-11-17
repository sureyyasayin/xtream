using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// Jellyfin "Kanallar" bolumunde Xtream canli TV kategorilerini klasor olarak gosterir.
/// Jellyfin'in Canli TV bolumu kanal kategorisi saklamadigi icin kategoriye gore gezinme
/// buradan yapilir. Kanal listesi senkronizasyonun yazdigi M3U dosyasindan okunur
/// (Xtream'e ek istek gitmez); yayin adresi imzali eklenti adresidir (sifre gorunmez).
/// </summary>
public partial class XtreamLiveChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey, ISupportsMediaProbe, IDisableMediaSourceDisplay
{
    public const string ChannelName = "Xtream Canlı TV";

    private static readonly object CacheLock = new();
    private static (DateTime Stamp, List<Entry> Entries) _cache;

    private readonly IHttpClientFactory _httpClientFactory;

    public XtreamLiveChannel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => ChannelName;

    public string Description => "Xtream canlı TV kanalları, kategorilere göre.";

    /// <summary>Ogelerin bicimi degisince artirilir (Jellyfin onbellegi yenilensin).</summary>
    private const int FormatVersion = 2;

    public string DataVersion => FormatVersion.ToString(CultureInfo.InvariantCulture) + "-" + Stamp().Ticks.ToString(CultureInfo.InvariantCulture);

    public string HomePageUrl => string.Empty;

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public string GetCacheKey(string? userId) => DataVersion;

    public bool IsEnabledFor(string userId) =>
        Plugin.Instance?.Configuration is { SyncLive: true, ShowLiveChannel: true } && File.Exists(Plugin.Instance.LiveM3uPath);

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video }
    };

    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        Task.FromResult(new DynamicImageResponse { HasImage = false });

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var entries = Load();
        var items = new List<ChannelItemInfo>();

        if (string.IsNullOrEmpty(query.FolderId))
        {
            // Kok: kategoriler, M3U'daki (Xtream'deki) sirayla.
            foreach (var group in entries.GroupBy(e => e.Group).OrderBy(g => g.Min(e => e.Number)))
            {
                // Jellyfin klasorleri ada gore siralar; numara araligi on eki Xtream sirasini korur.
                var first = group.Min(e => e.Number);
                var last = group.Max(e => e.Number);
                items.Add(new ChannelItemInfo
                {
                    Id = FolderId(group.Key),
                    Name = string.Create(CultureInfo.InvariantCulture, $"{first}–{last} · {group.Key}"),
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    ImageUrl = group.Select(e => e.Logo).FirstOrDefault(l => !string.IsNullOrEmpty(l))
                });
            }
        }
        else
        {
            foreach (var e in entries.Where(e => FolderId(e.Group) == query.FolderId).OrderBy(e => e.Number))
            {
                items.Add(new ChannelItemInfo
                {
                    Id = e.ItemId,
                    // Numara adin basinda: kumandada rakamla secim ve dogru siralama icin.
                    Name = e.Number.ToString(CultureInfo.InvariantCulture) + " · " + e.Name,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Clip,
                    IsLiveStream = true,
                    ImageUrl = e.Logo,
                    Overview = e.Group
                });
            }
        }

        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = items.Count });
    }

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance!.Configuration;
        var e = Load().FirstOrDefault(x => x.ItemId == id);
        if (e == null)
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var links = new StreamLinks(new XtreamClient(_httpClientFactory.CreateClient(), cfg), cfg);
        var source = new MediaSourceInfo
        {
            Id = id,
            Name = e.Name,
            Path = links.Live(e.StreamId, e.Ext),
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            IsInfiniteStream = true,
            Container = e.Ext == "m3u8" ? "hls" : "mpegts",
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            RequiresOpening = false,
            RequiresClosing = false
        };

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { source });
    }

    private static string FolderId(string group) =>
        "cat_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(group)), 0, 8).ToLowerInvariant();

    private static DateTime Stamp()
    {
        var path = Plugin.Instance?.LiveM3uPath;
        return path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    [GeneratedRegex("([a-zA-Z-]+)=\"([^\"]*)\"")]
    private static partial Regex Attributes();

    [GeneratedRegex(@"/live/[^/]+/[^/]+/(\d+)\.([A-Za-z0-9]+)$")]
    private static partial Regex LivePath();

    /// <summary>Senkronizasyonun yazdigi M3U dosyasini okur (degismedikce bellekten).</summary>
    private static List<Entry> Load()
    {
        var stamp = Stamp();
        lock (CacheLock)
        {
            if (_cache.Entries != null && _cache.Stamp == stamp)
            {
                return _cache.Entries;
            }
        }

        var list = new List<Entry>();
        var path = Plugin.Instance?.LiveM3uPath;
        if (path != null && File.Exists(path))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length - 1; i++)
            {
                if (!lines[i].StartsWith("#EXTINF", StringComparison.Ordinal))
                {
                    continue;
                }

                var m = LivePath().Match(lines[i + 1].Trim());
                if (!m.Success)
                {
                    continue;
                }

                var attrs = Attributes().Matches(lines[i]).ToDictionary(a => a.Groups[1].Value, a => a.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
                var comma = lines[i].LastIndexOf(',');
                var name = comma >= 0 ? lines[i][(comma + 1)..].Trim() : attrs.GetValueOrDefault("tvg-name", string.Empty);
                int.TryParse(attrs.GetValueOrDefault("tvg-chno"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number);
                var streamId = m.Groups[1].Value;
                var ext = m.Groups[2].Value.ToLowerInvariant();

                list.Add(new Entry(
                    "live_" + streamId + "_" + ext,
                    streamId,
                    ext,
                    number,
                    name,
                    attrs.GetValueOrDefault("group-title") is { Length: > 0 } g ? g : "Diğer",
                    attrs.GetValueOrDefault("tvg-logo")));
            }
        }

        lock (CacheLock)
        {
            _cache = (stamp, list);
        }

        return list;
    }

    private sealed record Entry(string ItemId, string StreamId, string Ext, int Number, string Name, string Group, string? Logo);
}
