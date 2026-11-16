using System.Text.Json;
using Jellyfin.Plugin.XtreamStrm.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
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

    public XtreamSyncTask(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IServerConfigurationManager configurationManager,
        ITaskManager taskManager,
        ILogger<XtreamSyncTask> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _configurationManager = configurationManager;
        _taskManager = taskManager;
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
            ? $" Alinamayan kategori: {run.FailedCategories} (sonraki senkronizasyonda yeniden denenir, bu turda silme yapilmadi)."
            : string.Empty;

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

    private async Task SyncMoviesAsync(XtreamClient client, PluginConfiguration cfg, RunState run, IProgress<double> progress, CancellationToken ct)
    {
        var cats = cfg.MovieCategoryIds;
        for (var i = 0; i < cats.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument doc;
            try
            {
                doc = await client.GetAsync("get_vod_streams", "&category_id=" + Uri.EscapeDataString(cats[i]), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tek kategori hatasi tum senkronizasyonu durdurmaz.
                run.FailedCategories++;
                _logger.LogWarning("Xtream STRM: film kategorisi {Category} alinamadi: {Error}", cats[i], ex.Message);
                continue;
            }

            using var docScope = doc;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
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

            progress.Report(30.0 * (i + 1) / cats.Length);
        }
    }

    private async Task SyncSeriesAsync(XtreamClient client, PluginConfiguration cfg, RunState run, IProgress<double> progress, CancellationToken ct)
    {
        var items = new Dictionary<string, SeriesItem>();
        foreach (var cat in cfg.SeriesCategoryIds)
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument doc;
            try
            {
                doc = await client.GetAsync("get_series", "&category_id=" + Uri.EscapeDataString(cat), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.FailedCategories++;
                _logger.LogWarning("Xtream STRM: dizi kategorisi {Category} alinamadi: {Error}", cat, ex.Message);
                continue;
            }

            using var docScope = doc;
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = XtreamClient.Str(el, "series_id");
                var name = XtreamClient.Str(el, "name");
                if (id == null || name == null || items.ContainsKey(id))
                {
                    continue;
                }

                items[id] = new SeriesItem(
                    id,
                    name,
                    XtreamClient.Str(el, "year") ?? XtreamClient.Str(el, "releaseDate") ?? XtreamClient.Str(el, "release_date"),
                    XtreamClient.Str(el, "tmdb") ?? XtreamClient.Str(el, "tmdb_id"),
                    XtreamClient.Str(el, "last_modified") ?? string.Empty);
            }
        }

        var done = 0;
        var gate = run.Gate;
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };

        await Parallel.ForEachAsync(items.Values, options, async (item, token) =>
        {
            var (baseName, folder) = Naming.Build(item.Name, item.Year, item.Tmdb, cfg.StripPrefixes);
            var dir = Path.Combine(cfg.SeriesPath, folder);

            List<string>? oldFiles;
            bool unchanged;
            lock (gate)
            {
                if (!run.Claim(dir))
                {
                    run.SkippedExisting++;
                    return;
                }

                run.Manifest.SeriesFiles.TryGetValue(item.Id, out oldFiles);
                unchanged = item.Stamp.Length > 0
                    && run.Manifest.SeriesStamp.TryGetValue(item.Id, out var oldStamp)
                    && oldStamp == item.Stamp
                    && oldFiles is { Count: > 0 };
            }

            // Degismemis dizi: bolum listesi tekrar indirilmez.
            if (unchanged && oldFiles!.All(f => f.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(f)))
            {
                lock (gate)
                {
                    run.Desired.UnionWith(oldFiles!);
                    run.NewSeriesFiles[item.Id] = oldFiles!;
                    run.NewSeriesStamp[item.Id] = item.Stamp;
                    run.Series++;
                    run.Episodes += oldFiles!.Count;
                }
            }
            else
            {
                try
                {
                    var files = new List<string>();
                    var written = 0;
                    using (var info = await client.GetAsync("get_series_info", "&series_id=" + Uri.EscapeDataString(item.Id), token).ConfigureAwait(false))
                    {
                        if (info.RootElement.ValueKind == JsonValueKind.Object && info.RootElement.TryGetProperty("episodes", out var eps))
                        {
                            var seen = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var ep in EnumerateEpisodes(eps, null))
                            {
                                var epId = XtreamClient.Str(ep.Element, "id");
                                if (epId == null)
                                {
                                    continue;
                                }

                                var season = ParseInt(XtreamClient.Str(ep.Element, "season")) ?? ep.SeasonHint ?? 1;
                                var number = ParseInt(XtreamClient.Str(ep.Element, "episode_num")) ?? (seen.Count + 1);
                                var ext = CleanExt(XtreamClient.Str(ep.Element, "container_extension"), "mkv");
                                var file = Path.Combine(dir, $"Season {season:00}", $"{baseName} S{season:00}E{number:00}.strm");
                                if (!seen.Add(file))
                                {
                                    continue;
                                }

                                if (WriteIfChanged(file, client.EpisodeUrl(epId, ext)))
                                {
                                    written++;
                                }

                                files.Add(file);
                            }
                        }
                    }

                    lock (gate)
                    {
                        run.Desired.UnionWith(files);
                        run.NewSeriesFiles[item.Id] = files;
                        run.NewSeriesStamp[item.Id] = item.Stamp;
                        run.Written += written;
                        run.Series++;
                        run.Episodes += files.Count;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Xtream STRM: '{Name}' dizisinin bolumleri alinamadi: {Error}", item.Name, ex.Message);
                    lock (gate)
                    {
                        run.FailedSeries++;
                        if (oldFiles != null)
                        {
                            // Hata durumunda eski bolumler korunur.
                            run.Desired.UnionWith(oldFiles);
                            run.NewSeriesFiles[item.Id] = oldFiles;
                        }
                    }
                }
            }

            var n = Interlocked.Increment(ref done);
            if (n % 10 == 0)
            {
                progress.Report(30 + (65.0 * n / items.Count));
            }
        }).ConfigureAwait(false);
    }

    /// <summary>"episodes" alani sezon->dizi sozlugu ya da ic ice dizi olabilir.</summary>
    private static IEnumerable<(JsonElement Element, int? SeasonHint)> EnumerateEpisodes(JsonElement el, int? seasonHint)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                foreach (var e in EnumerateEpisodes(child, seasonHint))
                {
                    yield return e;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("id", out _) && (el.TryGetProperty("episode_num", out _) || el.TryGetProperty("container_extension", out _)))
            {
                yield return (el, seasonHint);
            }
            else
            {
                foreach (var prop in el.EnumerateObject())
                {
                    foreach (var e in EnumerateEpisodes(prop.Value, ParseInt(prop.Name) ?? seasonHint))
                    {
                        yield return e;
                    }
                }
            }
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
