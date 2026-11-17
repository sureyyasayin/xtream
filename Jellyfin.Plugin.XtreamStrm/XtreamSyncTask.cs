using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.XtreamStrm.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>Xtream icerigini .strm dosyalari olarak medya klasorlerine yazan zamanlanmis gorev.</summary>
public class XtreamSyncTask : IScheduledTask
{
    private static readonly HashSet<string> MetadataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".xml", ".jpg", ".jpeg", ".png", ".webp", ".srt", ".ass", ".vtt", ".bif", ".strm", ".plexmatch"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<XtreamSyncTask> _logger;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ITaskManager _taskManager;
    private readonly ITunerHostManager _tunerHostManager;
    private StreamLinks? _links;

    public XtreamSyncTask(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IServerConfigurationManager configurationManager,
        ITaskManager taskManager,
        ITunerHostManager tunerHostManager,
        ILogger<XtreamSyncTask> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _configurationManager = configurationManager;
        _taskManager = taskManager;
        _tunerHostManager = tunerHostManager;
        _logger = logger;
    }

    public string Name => "Xtream STRM Senkronizasyonu";

    public string Key => "XtreamStrmSync";

    public string Description => "Xtream film ve dizilerini .strm kisayollari olarak medya klasorlerine yazar.";

    public string Category => "Xtream STRM";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Eklenti yuklenmedi.");
        var cfg = plugin.Configuration;

        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        var client = new XtreamClient(http, cfg);

        var manifest = Manifest.Load(plugin.ManifestPath);
        if (!client.IsConfigured)
        {
            _logger.LogWarning("Xtream STRM: adres/kullanici/sifre girilmemis, senkronizasyon atlandi.");
            manifest.LastRun = DateTime.UtcNow;
            manifest.LastMessage = "Xtream bilgileri girilmemis.";
            manifest.Save(plugin.ManifestPath);
            return;
        }

        if (cfg.HideCredentials && string.IsNullOrEmpty(cfg.StreamSecret))
        {
            cfg.StreamSecret = StreamLinks.NewSecret();
            plugin.SaveConfiguration();
        }

        _links = new StreamLinks(client, cfg);
        var linkNote = string.Empty;
        if (cfg.HideCredentials && !_links.Hidden)
        {
            linkNote = " UYARI: Jellyfin adresi bilinmedigi icin sifre .strm dosyalarinda acik kaldi (ayarlardan 'Jellyfin adresi'ni girin).";
            _logger.LogWarning("Xtream STRM: Jellyfin adresi bilinmiyor; .strm dosyalarina dogrudan Xtream adresi yaziliyor.");
        }

        var run = new RunState(manifest);
        var complete = true;
        string? error = null;

        try
        {
            if (cfg.SyncMovies && cfg.MovieCategoryIds.Length > 0)
            {
                await SyncMoviesAsync(client, cfg, run, progress, cancellationToken).ConfigureAwait(false);
            }

            progress.Report(30);

            if (cfg.SyncSeries && cfg.SeriesCategoryIds.Length > 0)
            {
                await SyncSeriesAsync(client, cfg, run, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Yarida kesildi: hicbir sey silinmez, yazilanlar kayda eklenir.
            SaveManifest(plugin, manifest, run, false, false, "Senkronizasyon iptal edildi.");
            throw;
        }
        catch (Exception ex)
        {
            complete = false;
            error = ex.Message;
            _logger.LogError(ex, "Xtream STRM: senkronizasyon hatasi");
        }

        progress.Report(95);

        // Canli TV, film/dizi senkronizasyonundan bagimsizdir.
        var liveText = string.Empty;
        try
        {
            var live = new LiveTvSync(_configurationManager, _logger);
            var (channels, liveChanged) = await live.RunAsync(client, cfg, plugin, cancellationToken).ConfigureAwait(false);
            liveText = $" Canli TV kanali: {channels}.";
            if (channels > 0)
            {
                var removed = await new StaleChannelCleaner(_libraryManager, _tunerHostManager, _logger).RunAsync(cancellationToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    liveText += $" Kaldirilan eski kanal: {removed}.";
                }
            }

            if (liveChanged)
            {
                QueueGuideRefresh();
            }
        }
        catch (OperationCanceledException)
        {
            SaveManifest(plugin, manifest, run, false, false, "Senkronizasyon iptal edildi.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Xtream STRM: canli TV senkronizasyon hatasi");
            liveText = " Canli TV HATASI: " + ex.Message;
            error ??= "Canli TV: " + ex.Message;
        }

        // Eksik veriyle temizlik yapilmaz: alinamayan kategorinin kisayollari silinmesin.
        var cleanup = complete && run.FailedCategories == 0 && cfg.RemoveOrphans;
        if (cleanup)
        {
            RemoveOrphans(manifest, run);
        }


        var failedCats = run.FailedCategories > 0
            ? " EKSIK: " + string.Join(" | ", run.Errors) + " (sonraki senkronizasyonda yeniden denenir, bu turda silme yapilmadi)."
            : string.Empty;

        if (client.Blocked)
        {
            failedCats += " Sunucu ust uste istekleri reddettigi icin bu tur erken durduruldu; bir sure sonra yeniden deneyin.";
        }

        var message = !complete
            ? $"HATA: {error} (hicbir dosya silinmedi).{liveText}"
            : $"Film: {run.Movies}, dizi: {run.Series}, bolum: {run.Episodes}. Yeni/guncellenen dosya: {run.Written}, silinen: {run.Deleted}, " +
              $"mevcut klasor oldugu icin atlanan: {run.SkippedExisting}, hatali dizi: {run.FailedSeries}.{failedCats}{liveText}{linkNote}";

        SaveManifest(plugin, manifest, run, complete, cleanup, message);
        _logger.LogInformation("Xtream STRM: {Message}", message);

        if (cfg.ScanLibraryAfterSync && (run.Written > 0 || run.Deleted > 0))
        {
            _libraryManager.QueueLibraryScan();
        }

        progress.Report(100);

        if (error != null)
        {
            throw new InvalidOperationException("Xtream STRM senkronizasyonu basarisiz: " + error);
        }
    }

    /// <summary>Jellyfin'in kendi "Rehberi Yenile" gorevini kuyruga alir (kanal listesi ve EPG yeniden okunur).</summary>
    private void QueueGuideRefresh()
    {
        try
        {
            var worker = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
            if (worker != null)
            {
                _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Xtream STRM: rehber yenileme baslatilamadi: {Error}", ex.Message);
        }
    }

    private static readonly Regex TvgNameRegex = new("tvg-name=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex GroupTitleRegex = new("group-title=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex LogoRegex = new("tvg-logo=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex SeriesUrlRegex = new(@"/series/[^/]+/[^/]+/(\d+)\.([A-Za-z0-9]{1,5})$", RegexOptions.Compiled);
    private static readonly Regex EpisodeRegex = new(@"^(.+?)\s+S(\d{1,3})\s*E(\d{1,4})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Ogenin secili kategorileri (category_id ve category_ids, tekrarsiz).</summary>
    private static List<string> SelectedCategories(JsonElement el, HashSet<string> selected)
    {
        var result = new List<string>();
        var cat = XtreamClient.Str(el, "category_id");
        if (cat != null && selected.Contains(cat))
        {
            result.Add(cat);
        }

        if (el.TryGetProperty("category_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                var s = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
                if (s != null && selected.Contains(s) && !result.Contains(s))
                {
                    result.Add(s);
                }
            }
        }

        return result;
    }

    /// <summary>Bir ogenin secili kategorilerden birinde olup olmadigi (category_id ya da category_ids).</summary>
    private static bool InSelected(JsonElement el, HashSet<string> selected)
    {
        var cat = XtreamClient.Str(el, "category_id");
        if (cat != null && selected.Contains(cat))
        {
            return true;
        }

        if (el.TryGetProperty("category_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                var s = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
                if (s != null && selected.Contains(s))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Tum filmleri TEK istekle alir ve secili kategorilere gore suzer. Saglayicilar dakikada
    /// ~30 istekten sonra engelledigi icin kategori basina ayri istek atilmaz.
    /// </summary>
    private async Task SyncMoviesAsync(XtreamClient client, PluginConfiguration cfg, RunState run, IProgress<double> progress, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await client.GetAsync("get_vod_streams", null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Fail("Film listesi alinamadi: " + ex.Message);
            _logger.LogWarning("Xtream STRM: film listesi alinamadi: {Error}", ex.Message);
            return;
        }

        using var scope = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            run.Fail("Film listesi beklenen bicimde degil.");
            return;
        }

        // Kategori adlari ve sirasi ("Xtream Filmler" kutusu icin); alinamazsa katalog bu tur yenilenmez.
        var catOrder = new List<string>();
        var catNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var catalogOk = true;
        try
        {
            using var cats = await client.GetAsync("get_vod_categories", null, ct).ConfigureAwait(false);
            if (cats.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cats.RootElement.EnumerateArray())
                {
                    if (XtreamClient.Str(c, "category_id") is string cid && !catNames.ContainsKey(cid))
                    {
                        catOrder.Add(cid);
                        catNames[cid] = (XtreamClient.Str(c, "category_name") ?? cid).Trim();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            catalogOk = false;
            _logger.LogWarning("Xtream STRM: film kategori adlari alinamadi: {Error}", ex.Message);
        }

        progress.Report(5);
        var selected = new HashSet<string>(cfg.MovieCategoryIds, StringComparer.Ordinal);
        var vod = new Dictionary<string, VodCategory>(StringComparer.Ordinal);
        var total = Math.Max(1, doc.RootElement.GetArrayLength());
        var index = 0;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if ((++index & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(5 + (25.0 * index / total));
            }

            if (!InSelected(el, selected))
            {
                continue;
            }

            var id = XtreamClient.Str(el, "stream_id");
            var name = XtreamClient.Str(el, "name");
            if (id == null || name == null)
            {
                continue;
            }

            var ext = CleanExt(XtreamClient.Str(el, "container_extension"), "mp4");
            var year = XtreamClient.Str(el, "year");
            var tmdb = XtreamClient.Str(el, "tmdb") ?? XtreamClient.Str(el, "tmdb_id");

            // "Xtream Filmler" kutusu: filmin secili her kategorisine (kutuphanedeki klasorden bagimsiz).
            var entry = new VodItem
            {
                Id = id,
                Name = Naming.Build(name, year, null, cfg.StripPrefixes).Base,
                Ext = ext,
                Icon = XtreamClient.Str(el, "stream_icon"),
                Year = ParseYear(year),
                Rating = ParseRating(XtreamClient.Str(el, "rating")),
                Tmdb = tmdb
            };
            foreach (var catId in SelectedCategories(el, selected))
            {
                if (!vod.TryGetValue(catId, out var vc))
                {
                    vod[catId] = vc = new VodCategory { Name = catNames.GetValueOrDefault(catId, catId) };
                }

                vc.Items.Add(entry);
            }

            var (baseName, folder) = Naming.Build(name, year, tmdb, cfg.StripPrefixes);

            var dir = Path.Combine(cfg.MoviesPath, folder);
            if (!run.Claim(dir))
            {
                run.SkippedExisting++;
                continue;
            }

            var file = Path.Combine(dir, baseName + ".strm");
            entry.Path = file;
            if (!run.Desired.Add(file))
            {
                continue; // ayni film birden fazla kez listelenmis
            }

            if (WriteIfChanged(file, _links!.Movie(id, ext)))
            {
                run.Written++;
            }

            run.Movies++;
        }

        if (catalogOk)
        {
            var catalog = new VodCatalog();
            foreach (var catId in catOrder.Concat(vod.Keys.Except(catOrder)))
            {
                if (vod.TryGetValue(catId, out var vc) && vc.Items.Count > 0)
                {
                    vc.Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                    catalog.Categories.Add(vc);
                }
            }

            CatalogStore.Save(Plugin.Instance!.VodCatalogPath, catalog);
        }
    }

    private static int? ParseYear(string? value) =>
        value != null && value.Length >= 4 && int.TryParse(value.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y > 1880 ? y : null;

    private static float? ParseRating(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : null;

    /// <summary>
    /// Dizileri saglayicinin M3U listesinden (tek istek, tum bolumler) okur. Dizi basina
    /// get_series_info istegi atmak binlerce istek demektir ve hiz sinirina takilir.
    /// </summary>
    private async Task SyncSeriesAsync(XtreamClient client, PluginConfiguration cfg, RunState run, IProgress<double> progress, CancellationToken ct)
    {
        // 1) Secili kategori kimliklerinin adlari (M3U'da bolumler kategori adiyla gruplanir).
        var selectedIds = new HashSet<string>(cfg.SeriesCategoryIds, StringComparer.Ordinal);
        var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categoryOrder = new List<string>();
        try
        {
            using var cats = await client.GetAsync("get_series_categories", null, ct).ConfigureAwait(false);
            if (cats.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in cats.RootElement.EnumerateArray())
                {
                    var id = XtreamClient.Str(el, "category_id");
                    var name = XtreamClient.Str(el, "category_name");
                    if (id != null && name != null && selectedIds.Contains(id) && selectedNames.Add(name.Trim()))
                    {
                        categoryOrder.Add(name.Trim());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Fail("Dizi kategorileri alinamadi: " + ex.Message);
            _logger.LogWarning("Xtream STRM: dizi kategorileri alinamadi: {Error}", ex.Message);
            return;
        }

        // 2) Dizi kunyeleri (yil, tmdb) - yalnizca daha iyi eslesme icin; alinamasa da devam edilir.
        var meta = new Dictionary<string, (string? Year, string? Tmdb, string? Cover, string? Plot, string? Rating)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var all = await client.GetAsync("get_series", null, ct).ConfigureAwait(false);
            if (all.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in all.RootElement.EnumerateArray())
                {
                    var name = XtreamClient.Str(el, "name");
                    if (name != null)
                    {
                        meta.TryAdd(
                            name.Trim(),
                            (XtreamClient.Str(el, "year") ?? XtreamClient.Str(el, "releaseDate") ?? XtreamClient.Str(el, "release_date"),
                             XtreamClient.Str(el, "tmdb") ?? XtreamClient.Str(el, "tmdb_id"),
                             XtreamClient.Str(el, "cover"),
                             XtreamClient.Str(el, "plot"),
                             XtreamClient.Str(el, "rating")));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Xtream STRM: dizi kunyeleri alinamadi, yil bilgisi olmadan devam ediliyor: {Error}", ex.Message);
        }

        progress.Report(35);

        // 3) Bolumler: M3U akisi satir satir okunur (onlarca MB olabilir, bellege alinmaz).
        var folders = new Dictionary<string, (string Base, string Dir, bool Claimed)>(StringComparer.OrdinalIgnoreCase);

        // "Xtream Diziler" kutusu: kategori -> dizi -> bolumler (kutuphanedeki klasorden bagimsiz).
        var catalog = new Dictionary<string, Dictionary<string, CatalogSeries>>(StringComparer.OrdinalIgnoreCase);
        var seenEpisodes = new HashSet<string>(StringComparer.Ordinal);
        var catalogOk = false;
        try
        {
            using var resp = await client.OpenM3uAsync(ct).ConfigureAwait(false);
            var length = resp.Content.Headers.ContentLength ?? 0;
            await using var raw = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            // Saglayici akisi yarida durdurursa sonsuza kadar beklenmez.
            await using var stream = new IdleTimeoutStream(raw, IdleTimeoutStream.DefaultTimeout, "Bolum listesi (M3U)");
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            string? pendingName = null;
            string? pendingGroup = null;
            string? pendingLogo = null;
            long read = 0;
            long lines = 0;

            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                read += line.Length + 1;
                if ((++lines & 4095) == 0)
                {
                    // Uzunluk bilinmiyorsa tipik liste boyutuna (50 MB) gore yaklasik ilerleme.
                    var ratio = Math.Min(1.0, read / (double)(length > 0 ? length : 50_000_000));
                    progress.Report(35 + (55.0 * ratio));
                }

                if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
                {
                    var nm = TvgNameRegex.Match(line);
                    var gm = GroupTitleRegex.Match(line);
                    pendingGroup = gm.Success ? gm.Groups[1].Value.Trim() : null;
                    var lm = LogoRegex.Match(line);
                    pendingLogo = lm.Success && lm.Groups[1].Value.Length > 0 ? lm.Groups[1].Value : null;
                    pendingName = nm.Success && nm.Groups[1].Value.Length > 0
                        ? nm.Groups[1].Value
                        : (line.LastIndexOf(',') is var c and >= 0 ? line[(c + 1)..] : null);
                    continue;
                }

                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var title = pendingName;
                var group = pendingGroup;
                var logo = pendingLogo;
                pendingName = null;
                pendingGroup = null;
                pendingLogo = null;

                if (title == null || group == null
                    || !line.Contains("/series/", StringComparison.Ordinal)
                    || !selectedNames.Contains(group))
                {
                    continue;
                }

                var m = EpisodeRegex.Match(title.Trim());
                if (!m.Success)
                {
                    continue;
                }

                var seriesName = m.Groups[1].Value.Trim();
                var season = int.Parse(m.Groups[2].Value);
                var number = int.Parse(m.Groups[3].Value);

                var um = SeriesUrlRegex.Match(line.Trim());
                if (um.Success && seenEpisodes.Add(group + "|" + seriesName + "|" + season + "|" + number))
                {
                    if (!catalog.TryGetValue(group, out var bucket))
                    {
                        catalog[group] = bucket = new Dictionary<string, CatalogSeries>(StringComparer.OrdinalIgnoreCase);
                    }

                    if (!bucket.TryGetValue(seriesName, out var si))
                    {
                        meta.TryGetValue(seriesName, out var sm);
                        bucket[seriesName] = si = new CatalogSeries
                        {
                            Name = Naming.Build(seriesName, sm.Year, null, cfg.StripPrefixes).Base,
                            Cover = sm.Cover ?? logo,
                            Plot = sm.Plot,
                            Year = ParseYear(sm.Year),
                            Rating = ParseRating(sm.Rating),
                            Tmdb = sm.Tmdb
                        };
                    }

                    si.Episodes.Add(new EpisodeItem { S = season, E = number, Id = um.Groups[1].Value, Ext = um.Groups[2].Value.ToLowerInvariant() });
                }

                if (!folders.TryGetValue(seriesName, out var f))
                {
                    meta.TryGetValue(seriesName, out var info);
                    var (baseName, folder) = Naming.Build(seriesName, info.Year, info.Tmdb, cfg.StripPrefixes);
                    var dir = Path.Combine(cfg.SeriesPath, folder);
                    var claimed = run.Claim(dir);
                    if (claimed)
                    {
                        run.Series++;
                    }
                    else
                    {
                        run.SkippedExisting++;
                    }

                    f = (baseName, dir, claimed);
                    folders[seriesName] = f;
                }

                if (!f.Claimed)
                {
                    continue;
                }

                if (catalog.TryGetValue(group, out var cb) && cb.TryGetValue(seriesName, out var cs) && cs.Dir == null)
                {
                    cs.Dir = f.Dir;
                    cs.Base = f.Base;
                }

                var file = Path.Combine(f.Dir, $"Season {season:00}", $"{f.Base} S{season:00}E{number:00}.strm");
                if (!run.Desired.Add(file))
                {
                    continue; // ayni bolum iki kez listelenmis
                }

                if (WriteIfChanged(file, _links!.FromM3u(line.Trim())))
                {
                    run.Written++;
                }

                run.Episodes++;
            }

            catalogOk = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Fail("Bolum listesi (M3U) alinamadi ya da yarim kaldi: " + ex.Message);
            _logger.LogWarning("Xtream STRM: bolum listesi (M3U) okunamadi: {Error}", ex.Message);
        }

        // Yarim kalan listeyle katalog yenilenmez (kutu bosalmasin).
        if (catalogOk)
        {
            var result = new SeriesCatalog();
            foreach (var name in categoryOrder.Concat(catalog.Keys.Except(categoryOrder, StringComparer.OrdinalIgnoreCase)))
            {
                if (catalog.TryGetValue(name, out var bucket) && bucket.Count > 0)
                {
                    result.Categories.Add(new SeriesCategory
                    {
                        Name = name,
                        Series = bucket.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                    });
                }
            }

            CatalogStore.Save(Plugin.Instance!.SeriesCatalogPath, result);
        }
    }

    private void RemoveOrphans(Manifest manifest, RunState run)
    {
        foreach (var file in manifest.Files)
        {
            if (run.Desired.Contains(file) || !file.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    run.Deleted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Xtream STRM: {File} silinemedi: {Error}", file, ex.Message);
            }
        }

        // Eklentinin actigi ve artik kullanilmayan klasorler: icinde yalnizca
        // metadata (nfo/resim/altyazi) kaldiysa silinir, baska dosya varsa dokunulmaz.
        foreach (var dir in manifest.Dirs)
        {
            if (run.Claimed.Contains(dir))
            {
                continue;
            }

            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var leftovers = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
                var onlyMetadata = leftovers.All(f =>
                    MetadataExtensions.Contains(Path.GetExtension(f)) && !f.EndsWith(".strm", StringComparison.OrdinalIgnoreCase));
                if (onlyMetadata)
                {
                    Directory.Delete(dir, true);
                }
                else
                {
                    run.KeptDirs.Add(dir);
                }
            }
            catch (Exception ex)
            {
                run.KeptDirs.Add(dir);
                _logger.LogWarning("Xtream STRM: {Dir} klasoru silinemedi: {Error}", dir, ex.Message);
            }
        }
    }

    private static void SaveSnapshot(RunState run, string message)
    {
        var old = run.Manifest;
        var files = new HashSet<string>(run.Desired, StringComparer.Ordinal);
        files.UnionWith(old.Files);
        var dirs = new HashSet<string>(run.Claimed, StringComparer.Ordinal);
        dirs.UnionWith(old.Dirs);

        var seriesFiles = new Dictionary<string, List<string>>(old.SeriesFiles);
        foreach (var kv in run.NewSeriesFiles)
        {
            seriesFiles[kv.Key] = kv.Value;
        }

        var stamps = new Dictionary<string, string>(old.SeriesStamp);
        foreach (var kv in run.NewSeriesStamp)
        {
            stamps[kv.Key] = kv.Value;
        }

        new Manifest
        {
            Files = files.ToList(),
            Dirs = dirs.ToList(),
            SeriesFiles = seriesFiles,
            SeriesStamp = stamps,
            LastRun = DateTime.UtcNow,
            LastMessage = message
        }.Save(Plugin.Instance!.ManifestPath);
    }

    private static void SaveManifest(Plugin plugin, Manifest manifest, RunState run, bool complete, bool cleaned, string message)
    {
        var files = new HashSet<string>(run.Desired, StringComparer.Ordinal);
        var dirs = new HashSet<string>(run.Claimed, StringComparer.Ordinal);
        dirs.UnionWith(run.KeptDirs);

        if (!cleaned)
        {
            // Temizlik yapilmadiysa eski kayitlar korunur.
            files.UnionWith(manifest.Files);
            dirs.UnionWith(manifest.Dirs);
            foreach (var kv in manifest.SeriesFiles)
            {
                run.NewSeriesFiles.TryAdd(kv.Key, kv.Value);
            }
        }

        manifest.Files = files.ToList();
        manifest.Dirs = dirs.ToList();
        manifest.SeriesFiles = run.NewSeriesFiles;
        manifest.SeriesStamp = run.NewSeriesStamp;
        manifest.LastRun = DateTime.UtcNow;
        manifest.LastMessage = message;
        manifest.Save(plugin.ManifestPath);
    }

    private static bool WriteIfChanged(string file, string content)
    {
        if (File.Exists(file) && File.ReadAllText(file).Trim() == content)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return true;
    }

    private static string CleanExt(string? ext, string fallback)
    {
        ext = ext?.Trim().TrimStart('.').ToLowerInvariant();
        return !string.IsNullOrEmpty(ext) && ext.Length <= 5 && ext.All(char.IsLetterOrDigit) ? ext : fallback;
    }

    private static int? ParseInt(string? s) => int.TryParse(s, out var n) && n >= 0 ? n : null;

    private sealed record SeriesItem(string Id, string Name, string? Year, string? Tmdb, string Stamp);

    private sealed class RunState
    {
        private readonly HashSet<string> _ownedDirs;

        public RunState(Manifest manifest)
        {
            Manifest = manifest;
            _ownedDirs = new HashSet<string>(manifest.Dirs, StringComparer.Ordinal);
        }

        public object Gate { get; } = new();

        public Manifest Manifest { get; }

        public HashSet<string> Desired { get; } = new(StringComparer.Ordinal);


        public HashSet<string> Claimed { get; } = new(StringComparer.Ordinal);

        public HashSet<string> KeptDirs { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> NewSeriesFiles { get; } = new();

        public Dictionary<string, string> NewSeriesStamp { get; } = new();

        public int Movies { get; set; }

        public int Series { get; set; }

        public int Episodes { get; set; }

        public int Written { get; set; }

        public int Deleted { get; set; }

        public int SkippedExisting { get; set; }

        public int FailedSeries { get; set; }

        public int FailedCategories { get; set; }

        public List<string> Errors { get; } = new();

        /// <summary>Bir asama tamamlanamadi: bu turda temizlik yapilmaz.</summary>
        public void Fail(string message)
        {
            FailedCategories++;
            Errors.Add(message);
        }

        /// <summary>
        /// Klasoru eklenti adina sahiplenir. Klasor zaten varsa ve eklenti acmadiysa
        /// (kullanicinin kendi filmi/dizisi) false doner ve icine yazilmaz.
        /// </summary>
        public bool Claim(string dir)
        {
            if (Claimed.Contains(dir))
            {
                return true;
            }

            if (Directory.Exists(dir) && !_ownedDirs.Contains(dir))
            {
                return false;
            }

            Claimed.Add(dir);
            return true;
        }
    }
}
