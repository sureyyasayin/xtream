using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// Xtream tuner'inin listesinde artik bulunmayan kanal kayitlarini Jellyfin veritabanindan siler.
/// Jellyfin kanal kimligini yayin adresinden uretir; hesap ya da adres degisince eski kanallar
/// veritabaninda kalir, listede ayni isimle iki kez gorunur ve acilmaya calisilinca
/// "Unable to find host to play channel" hatasi verir.
/// </summary>
public class StaleChannelCleaner
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly ILogger _logger;

    public StaleChannelCleaner(ILibraryManager libraryManager, ITunerHostManager tunerHostManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _tunerHostManager = tunerHostManager;
        _logger = logger;
    }

    /// <returns>Silinen kanal sayisi.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? prefix = null;

        foreach (var host in _tunerHostManager.TunerHosts.Where(h => string.Equals(h.Type, "m3u", StringComparison.OrdinalIgnoreCase)))
        {
            var channels = await host.GetChannels(false, ct).ConfigureAwait(false);
            foreach (var ch in channels)
            {
                if (!string.Equals(ch.TunerHostId, LiveTvSync.TunerId, StringComparison.OrdinalIgnoreCase) || ch.Id == null || ch.Id.Length <= 32)
                {
                    continue;
                }

                // Kimlik = tuner'a ozgu on ek + adresin ozeti (32 karakter).
                var p = ch.Id.Substring(0, ch.Id.Length - 32);
                if (prefix == null)
                {
                    prefix = p;
                }
                else if (!string.Equals(prefix, p, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Xtream STRM: kanal kimlikleri beklenen bicimde degil, eski kanal temizligi atlandi.");
                    return 0;
                }

                current.Add(ch.Id);
            }
        }

        // Liste okunamadiysa hicbir sey silinmez.
        if (prefix == null || current.Count == 0)
        {
            return 0;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel }
        });

        var stale = items
            .Where(i => i.ExternalId != null
                && i.ExternalId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !current.Contains(i.ExternalId))
            .ToList();

        var deleted = 0;
        foreach (var item in stale)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Xtream STRM: eski kanal silinemedi ({Name}): {Error}", item.Name, ex.Message);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Xtream STRM: listede olmayan {Count} eski kanal kaldirildi (guncel kanal: {Current}).", deleted, current.Count);
        }

        return deleted;
    }
}

/// <summary>
/// 1.5.x'te "Xtream Filmler / Diziler" ogeleri film/dizi/bolum olarak olusuyordu; Jellyfin bunlar
/// icin TMDb/TheTVDB aramasi yapip kutuphane taramasini saatlerce uzatiyordu. Artik duz video ve
/// klasor olarak olusuyorlar; eski turdeki ogeler bir kez silinir (kutu acilinca yenileri olusur).
/// Kutuphanedeki filmlere ve dizilere dokunulmaz (yalnizca bu iki kutunun ogeleri).
/// </summary>
public static class OldChannelItems
{
    public static int Remove(ILibraryManager libraryManager, ILogger logger)
    {
        var channelIds = libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Channel } })
            .Where(c => c.Name is "Xtream Filmler" or "Xtream Diziler")
            .Select(c => c.Id)
            .ToArray();
        if (channelIds.Length == 0)
        {
            return 0;
        }

        // Once yapraklar, sonra klasorler (dizi silinince altindakiler de gider).
        var order = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Season, BaseItemKind.Series };
        var removed = 0;
        foreach (var kind in order)
        {
            var items = libraryManager.GetItemList(new InternalItemsQuery
            {
                ChannelIds = channelIds,
                IncludeItemTypes = new[] { kind },
                Recursive = true
            });

            foreach (var item in items)
            {
                try
                {
                    libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, false);
                    removed++;
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Xtream STRM: kutu ogesi silinemedi ({Name}): {Error}", item.Name, ex.Message);
                }
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Xtream STRM: Xtream Filmler / Diziler kutularindaki {Count} eski oge kaldirildi (meta veri aramasi yapmayan yeni ogeler olusacak).", removed);
        }

        return removed;
    }
}

/// <summary>Jellyfin acildiktan kisa sure sonra eski kanal temizligini bir kez calistirir.</summary>
public class StaleChannelCleanupService : BackgroundService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly ILogger<StaleChannelCleanupService> _logger;

    public StaleChannelCleanupService(ILibraryManager libraryManager, ITunerHostManager tunerHostManager, ILogger<StaleChannelCleanupService> logger)
    {
        _libraryManager = libraryManager;
        _tunerHostManager = tunerHostManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Canli TV ve kutuphane acilisini bitirsin.
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken).ConfigureAwait(false);
            OldChannelItems.Remove(_libraryManager, _logger);
            if (Plugin.Instance?.Configuration.SyncLive != true)
            {
                return;
            }

            await new StaleChannelCleaner(_libraryManager, _tunerHostManager, _logger).RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Xtream STRM: eski kanal temizligi yapilamadi: {Error}", ex.Message);
        }
    }
}
