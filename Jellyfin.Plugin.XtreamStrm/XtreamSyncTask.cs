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
              $"mevcut klasor oldugu icin atlanan: {run.SkippedExisting}, hatali dizi: {run.FailedSeries}.{failedCats}{liveText}";

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
    private static readonly Regex EpisodeRegex = new(@"^(.+?)\s+S(\d{1,3})\s*E(\d{1,4})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        progress.Report(5);
        var selected = new HashSet<string>(cfg.MovieCategoryIds, StringComparer.Ordinal);
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
            var (baseName, folder) = Naming.Build(
                name,
                XtreamClient.Str(el, "year"),
                XtreamClient.Str(el, "tmdb") ?? XtreamClient.Str(el, "tmdb_id"),
                cfg.StripPrefixes);

            var dir = Path.Combine(cfg.MoviesPath, folder);
            if (!run.Claim(dir))
            {
                run.SkippedExisting++;
                continue;
            }

            var file = Path.Combine(dir, baseName + ".strm");
            if (!run.Desired.Add(file))
            {
                continue; // ayni film birden fazla kez listelenmis
            }

            if (WriteIfChanged(file, client.MovieUrl(id, ext)))
            {
                run.Written++;
            }

            run.Movies++;
        }
    }

    /// <summary>
    /// Dizileri saglayicinin M3U listesinden (tek istek, tum bolumler) okur. Dizi basina
    /// get_series_info istegi atmak binlerce istek demektir ve hiz sinirina takilir.
    /// </summary>
    private async Task SyncSeriesAsync(XtreamClient client, PluginConfiguration cfg, RunState run, IProgress<double> progress, CancellationToken ct)
    {
        // 1) Secili kategori kimliklerinin adlari (M3U'da bolumler kategori adiyla gruplanir).
        var selectedIds = new HashSet<string>(cfg.SeriesCategoryIds, StringComparer.Ordinal);
        var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cats = await client.GetAsync("get_series_categories", null, ct).ConfigureAwait(false);
            if (cats.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in cats.RootElement.EnumerateArray())
                {
                    var id = XtreamClient.Str(el, "category_id");
                    var name = XtreamClient.Str(el, "category_name");
                    if (id != null && name != null && selectedIds.Contains(id))
                    {
                        selectedNames.Add(name.Trim());
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
        var meta = new Dictionary<string, (string? Year, string? Tmdb)>(StringComparer.OrdinalIgnoreCase);
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
                             XtreamClient.Str(el, "tmdb") ?? XtreamClient.Str(el, "tmdb_id")));
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
        try
        {
            using var resp = await client.OpenM3uAsync(ct).ConfigureAwait(false);
            var length = resp.Content.Headers.ContentLength ?? 0;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            string? pendingName = null;
            string? pendingGroup = null;
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
                pendingName = null;
                pendingGroup = null;

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

                var file = Path.Combine(f.Dir, $"Season {season:00}", $"{f.Base} S{season:00}E{number:00}.strm");
                if (!run.Desired.Add(file))
                {
                    continue; // ayni bolum iki kez listelenmis
                }

                if (WriteIfChanged(file, line.Trim()))
                {
                    run.Written++;
                }

                run.Episodes++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Fail("Bolum listesi (M3U) alinamadi ya da yarim kaldi: " + ex.Message);
            _logger.LogWarning("Xtream STRM: bolum listesi (M3U) okunamadi: {Error}", ex.Message);
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
