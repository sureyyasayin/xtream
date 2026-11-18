using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// "Xtream Filmler" / "Xtream Diziler" kutularinin ortak kismi. Kutuphanedeki filmler ve
/// diziler oldugu gibi kalir; bu kutular Xtream kategorilerine gore ayri bir gezinme sunar.
/// Veriler senkronizasyonun yazdigi katalog dosyalarindan okunur (Xtream'e ek istek gitmez),
/// yayin adresi imzali eklenti adresidir (sifre gorunmez).
/// </summary>
public abstract class XtreamCatalogChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey, IDisableMediaSourceDisplay
{
    /// <summary>Ogelerin bicimi degisince artirilir (Jellyfin onbellegi yenilensin).</summary>
    // 2: ogeler duz video/klasor (TMDb/TVDb aramasi yapilmasin; tiklaninca kutuphane karti acilir).
    private const int FormatVersion = 2;

    private readonly IHttpClientFactory _httpClientFactory;

    protected XtreamCatalogChannel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public abstract string Name { get; }

    public abstract string Description { get; }

    protected abstract string CatalogPath { get; }

    protected abstract bool Enabled { get; }

    public string DataVersion => FormatVersion.ToString(CultureInfo.InvariantCulture) + "-" + CatalogStore.Stamp(CatalogPath).Ticks.ToString(CultureInfo.InvariantCulture);

    public string HomePageUrl => string.Empty;

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public string GetCacheKey(string? userId) => DataVersion;

    public bool IsEnabledFor(string userId) => Plugin.Instance != null && Enabled && File.Exists(CatalogPath);

    public abstract InternalChannelFeatures GetChannelFeatures();

    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        Task.FromResult(new DynamicImageResponse { HasImage = false });

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var items = GetItems(query.FolderId);
        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = items.Count });
    }

    protected abstract List<ChannelItemInfo> GetItems(string? folderId);

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        // Kimlik: "{tur}_{xtreamId}_{uzanti}"
        var parts = id.Split('_');
        if (parts.Length != 3 || Plugin.Instance == null)
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var cfg = Plugin.Instance.Configuration;
        var links = new StreamLinks(new XtreamClient(_httpClientFactory.CreateClient(), cfg), cfg);
        var path = parts[0] == "vod" ? links.Movie(parts[1], parts[2]) : links.Episode(parts[1], parts[2]);

        var source = new MediaSourceInfo
        {
            Id = id,
            Path = path,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Container = parts[2],
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            RequiresOpening = false,
            RequiresClosing = false
        };

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { source });
    }

    /// <summary>Jellyfin klasorleri ada gore siralar; sira numarasi Xtream sirasini korur.</summary>
    protected static string Numbered(int index, string name) =>
        (index + 1).ToString(CultureInfo.InvariantCulture) + " · " + name;
}

/// <summary>"Xtream Filmler": kategori klasorleri -> filmler.</summary>
public sealed class XtreamVodChannel : XtreamCatalogChannel
{
    public XtreamVodChannel(IHttpClientFactory httpClientFactory)
        : base(httpClientFactory)
    {
    }

    public override string Name => "Xtream Filmler";

    public override string Description => "Xtream filmleri, kategorilere göre.";

    protected override string CatalogPath => Plugin.Instance!.VodCatalogPath;

    protected override bool Enabled => Plugin.Instance!.Configuration is { SyncMovies: true, ShowVodChannel: true };

    public override InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video }
    };

    protected override List<ChannelItemInfo> GetItems(string? folderId)
    {
        var catalog = CatalogStore.Load<VodCatalog>(CatalogPath);
        var items = new List<ChannelItemInfo>();

        if (string.IsNullOrEmpty(folderId))
        {
            for (var i = 0; i < catalog.Categories.Count; i++)
            {
                var c = catalog.Categories[i];
                items.Add(new ChannelItemInfo
                {
                    Id = "vc_" + CatalogStore.Hash(c.Name),
                    Name = Numbered(i, c.Name),
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    ImageUrl = c.Items.Select(x => x.Icon).FirstOrDefault(x => !string.IsNullOrEmpty(x))
                });
            }

            return items;
        }

        var cat = catalog.Categories.FirstOrDefault(c => "vc_" + CatalogStore.Hash(c.Name) == folderId);
        foreach (var m in cat?.Items ?? new List<VodItem>())
        {
            items.Add(new ChannelItemInfo
            {
                Id = "vod_" + m.Id + "_" + m.Ext,
                Name = m.Name,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                ImageUrl = m.Icon,
                ProductionYear = m.Year,
                CommunityRating = m.Rating,
            });
        }

        return items;
    }
}

/// <summary>"Xtream Diziler": kategori klasorleri -> diziler -> sezonlar -> bolumler.</summary>
public sealed class XtreamSeriesChannel : XtreamCatalogChannel
{
    public XtreamSeriesChannel(IHttpClientFactory httpClientFactory)
        : base(httpClientFactory)
    {
    }

    public override string Name => "Xtream Diziler";

    public override string Description => "Xtream dizileri, kategorilere göre.";

    protected override string CatalogPath => Plugin.Instance!.SeriesCatalogPath;

    protected override bool Enabled => Plugin.Instance!.Configuration is { SyncSeries: true, ShowSeriesChannel: true };

    public override InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video }
    };

    private static string CategoryId(SeriesCategory c) => "sc_" + CatalogStore.Hash(c.Name);

    internal static string SeriesId(SeriesCategory c, CatalogSeries s) => "sr_" + CatalogStore.Hash(c.Name + "|" + s.Name);

    internal static string SeasonId(SeriesCategory c, CatalogSeries s, int season) =>
        "ss_" + CatalogStore.Hash(c.Name + "|" + s.Name) + "-" + season.ToString(CultureInfo.InvariantCulture);

    protected override List<ChannelItemInfo> GetItems(string? folderId)
    {
        var catalog = CatalogStore.Load<SeriesCatalog>(CatalogPath);
        var items = new List<ChannelItemInfo>();

        if (string.IsNullOrEmpty(folderId))
        {
            for (var i = 0; i < catalog.Categories.Count; i++)
            {
                var c = catalog.Categories[i];
                items.Add(new ChannelItemInfo
                {
                    Id = CategoryId(c),
                    Name = Numbered(i, c.Name),
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    ImageUrl = c.Series.Select(x => x.Cover).FirstOrDefault(x => !string.IsNullOrEmpty(x))
                });
            }

            return items;
        }

        foreach (var c in catalog.Categories)
        {
            if (folderId == CategoryId(c))
            {
                foreach (var s in c.Series)
                {
                    items.Add(new ChannelItemInfo
                    {
                        Id = SeriesId(c, s),
                        Name = s.Name,
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        ImageUrl = s.Cover,
                        Overview = s.Plot,
                        ProductionYear = s.Year,
                        CommunityRating = s.Rating,
                    });
                }

                return items;
            }

            foreach (var s in c.Series)
            {
                if (folderId == SeriesId(c, s))
                {
                    foreach (var season in s.Episodes.Select(e => e.S).Distinct().OrderBy(n => n))
                    {
                        items.Add(new ChannelItemInfo
                        {
                            Id = SeasonId(c, s, season),
                            Name = "Sezon " + season.ToString(CultureInfo.InvariantCulture),
                            Type = ChannelItemType.Folder,
                            FolderType = ChannelFolderType.Container,
                            IndexNumber = season,
                            ImageUrl = s.Cover,
                            Overview = s.Plot
                        });
                    }

                    return items;
                }

                if (folderId.StartsWith("ss_", StringComparison.Ordinal) && folderId.StartsWith(SeasonId(c, s, 0)[..^1], StringComparison.Ordinal))
                {
                    var season = int.Parse(folderId[(folderId.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);
                    foreach (var e in s.Episodes.Where(e => e.S == season).OrderBy(e => e.E))
                    {
                        items.Add(new ChannelItemInfo
                        {
                            Id = "ep_" + e.Id + "_" + e.Ext,
                            Name = "Bölüm " + e.E.ToString(CultureInfo.InvariantCulture),
                            Type = ChannelItemType.Media,
                            MediaType = ChannelMediaType.Video,
                            ContentType = ChannelMediaContentType.Clip,
                            SeriesName = s.Name,
                            IndexNumber = e.E,
                            ParentIndexNumber = season,
                            ImageUrl = s.Cover
                        });
                    }

                    return items;
                }
            }
        }

        return items;
    }
}
