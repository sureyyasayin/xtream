using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// Saglayicida bos gelen (dosyasi bozuk ya da kaldirilmis) bir film icin ayni adli, ayni yilli
/// baska bir kopyayi bulur. Cogu saglayici ayni filmi birkac kategoride (HD, 4K...) ayri
/// kayitlarla tutar; biri bozuksa digeri genellikle calisir.
/// Sonuc 10 dakika akilda tutulur (oynatici ayni adresi defalarca ister).
/// </summary>
public sealed partial class MovieFallback
{
    private static readonly ConcurrentDictionary<string, (string Id, string Ext, DateTime Until)> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    public MovieFallback(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\((19|20)\d{2}\)|\b(19|20)\d{2}\b|\b(4k|uhd|fhd|hd|sd|hevc|h265|x265|1080p|720p|2160p)\b")]
    private static partial Regex NoiseRegex();

    public async Task<(string Id, string Ext)> ResolveAsync(XtreamClient client, string id, string ext, CancellationToken ct)
    {
        if (Cache.TryGetValue(id, out var cached) && cached.Until > DateTime.UtcNow)
        {
            return (cached.Id, cached.Ext);
        }

        var result = (Id: id, Ext: ext);
        if (!await HasDataAsync(client.MovieUrl(id, ext), ct).ConfigureAwait(false))
        {
            foreach (var alt in Alternatives(id, ext).Concat(TmdbAlternatives(id)).DistinctBy(a => a.Id).Take(8))
            {
                if (await HasDataAsync(client.MovieUrl(alt.Id, alt.Ext), ct).ConfigureAwait(false))
                {
                    _logger.LogInformation("Xtream STRM: film {Id} saglayicida bos, ayni filmin baska kopyasi ({Alt}) aciliyor.", id, alt.Id);
                    result = (alt.Id, alt.Ext);
                    break;
                }
            }
        }

        Cache[id] = (result.Id, result.Ext, DateTime.UtcNow + CacheTime);
        return result;
    }

    /// <summary>Adresin gercekten veri gonderip gondermedigi (ilk bayt istenir, baglanti hemen kapatilir).</summary>
    private async Task<bool> HasDataAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", XtreamClient.UserAgent);
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[1];
            return await body.ReadAsync(buffer, ct).ConfigureAwait(false) > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Katalogdaki ayni adli (yil ve kalite etiketleri haric) kopyalar; ayni uzantili olanlar once.</summary>
    private static List<VodItem> Alternatives(string id, string ext)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return new List<VodItem>();
        }

        var all = CatalogStore.Load<VodCatalog>(plugin.VodCatalogPath).Categories
            .SelectMany(c => c.Items)
            .GroupBy(i => i.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var me = all.FirstOrDefault(i => i.Id == id);
        if (me == null)
        {
            return new List<VodItem>();
        }

        var key = Key(me.Name);
        var year = YearOf(me);
        return all
            .Where(i => i.Id != id && Key(i.Name) == key && (year == null || YearOf(i) == null || YearOf(i) == year))
            .OrderBy(i => string.Equals(i.Ext, ext, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(5)
            .ToList();
    }

    /// <summary>
    /// Saglayici ayni filmi farkli adlarla verebilir ("Ay Dogarken" / "Moonrise"). Jellyfin ikisini de
    /// ayni TMDb kaydina eslediyse, kutuphanedeki ayni TMDb kimlikli diger filmlerin kopyalari.
    /// </summary>
    private List<VodItem> TmdbAlternatives(string id)
    {
        var result = new List<VodItem>();
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return result;
        }

        var all = CatalogStore.Load<VodCatalog>(plugin.VodCatalogPath).Categories.SelectMany(c => c.Items).ToList();
        var me = all.FirstOrDefault(i => i.Id == id);
        if (me?.Path == null)
        {
            return result;
        }

        string? tmdb = null;
        _libraryManager.FindByPath(me.Path, false)?.ProviderIds.TryGetValue("Tmdb", out tmdb);
        tmdb ??= me.Tmdb;
        if (string.IsNullOrEmpty(tmdb))
        {
            return result;
        }

        var byPath = all.Where(i => i.Path != null && i.Id != id)
            .GroupBy(i => i.Path!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var others = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = tmdb },
            Recursive = true
        });

        foreach (var o in others)
        {
            if (o.Path != null && o.Path != me.Path && byPath.TryGetValue(o.Path, out var v))
            {
                result.Add(v);
            }
        }

        result.AddRange(all.Where(i => i.Id != id && i.Tmdb == tmdb));
        return result;
    }

    private static int? YearOf(VodItem item)
    {
        if (item.Year != null)
        {
            return item.Year;
        }

        var m = YearRegex().Match(item.Name);
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    private static string Key(string name)
    {
        var lower = NoiseRegex().Replace(name.ToLower(CultureInfo.GetCultureInfo("tr-TR")), " ");
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
