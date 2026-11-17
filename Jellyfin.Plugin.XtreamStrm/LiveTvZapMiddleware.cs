using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// Kanal degistirmeyi tek baglantili hesapta mumkun kilar.
///
/// Jellyfin istemcisi kanal degistirirken once yeni kanalin yayinini ister, eskisini ancak
/// birkac saniye sonra kapatir. Tuner sayisi 1 iken bu, "M3U simultaneous stream limit has
/// been reached" hatasina yol acar. Bu ara katman, bir cihaz yeni bir canli kanal istediginde
/// ayni cihazin hala acik duran eski canli yayinini once kapatir.
/// </summary>
public partial class LiveTvZapMiddleware
{
    /// <summary>Cihaz basina son acilan canli yayin.</summary>
    private static readonly ConcurrentDictionary<string, OpenedStream> OpenedStreams = new(StringComparer.Ordinal);

    private readonly RequestDelegate _next;
    private readonly ILogger<LiveTvZapMiddleware> _logger;

    public LiveTvZapMiddleware(RequestDelegate next, ILogger<LiveTvZapMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    [GeneratedRegex(@"/Items/([0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12})/PlaybackInfo$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaybackInfoPath();

    public async Task InvokeAsync(
        HttpContext context,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IMediaSourceManager mediaSourceManager,
        IAuthorizationContext authorizationContext)
    {
        string? deviceId = null;
        Guid itemId = Guid.Empty;

        if (HttpMethods.IsPost(context.Request.Method) && Plugin.Instance?.Configuration.LiveRemoteControl == true)
        {
            var match = PlaybackInfoPath().Match(context.Request.Path.Value ?? string.Empty);
            if (match.Success && Guid.TryParse(match.Groups[1].Value, out itemId) && libraryManager.GetItemById(itemId) is LiveTvChannel)
            {
                try
                {
                    var auth = await authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
                    if (auth.IsAuthenticated && !string.IsNullOrEmpty(auth.DeviceId))
                    {
                        deviceId = auth.DeviceId;
                        await CloseOtherLiveStreamsAsync(deviceId, itemId, libraryManager, sessionManager, mediaSourceManager).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // Basarisiz olursa istek normal akisina devam eder.
                    _logger.LogWarning("Xtream STRM: onceki canli yayin kapatilamadi: {Error}", ex.Message);
                }
            }
        }

        if (deviceId == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Yanittan acilan canli yayinin kimligini al (cihaz ilerleme bildirimini gec gonderebilir).
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
        var response = context.Response;
        var original = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = original;
        }

        if (response.StatusCode == StatusCodes.Status200OK)
        {
            var liveStreamId = ReadLiveStreamId(buffer);
            if (liveStreamId != null)
            {
                OpenedStreams[deviceId] = new OpenedStream(itemId, liveStreamId);
                _logger.LogInformation("Xtream STRM: canli yayin acildi ({Name}).", libraryManager.GetItemById(itemId)?.Name);
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task CloseOtherLiveStreamsAsync(
        string deviceId,
        Guid itemId,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IMediaSourceManager mediaSourceManager)
    {
        var candidates = new List<(string Id, string? Name)>();
        foreach (var session in sessionManager.Sessions)
        {
            var playing = session.NowPlayingItem;
            var id = session.PlayState?.LiveStreamId;
            if (string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal)
                && playing != null && playing.Id != itemId && !string.IsNullOrEmpty(id))
            {
                candidates.Add((id, playing.Name));
            }
        }

        if (OpenedStreams.TryGetValue(deviceId, out var opened) && opened.ItemId != itemId)
        {
            candidates.Add((opened.LiveStreamId, libraryManager.GetItemById(opened.ItemId)?.Name));
            OpenedStreams.TryRemove(deviceId, out _);
        }

        // Baska bir cihazin izledigi yayina dokunma (paylasilan yayin).
        var usedByOthers = new HashSet<string>(
            sessionManager.Sessions
                .Where(s => !string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal) && !string.IsNullOrEmpty(s.PlayState?.LiveStreamId))
                .Select(s => s.PlayState!.LiveStreamId!),
            StringComparer.Ordinal);

        var newName = libraryManager.GetItemById(itemId)?.Name;
        foreach (var (id, name) in candidates.DistinctBy(c => c.Id))
        {
            if (usedByOthers.Contains(id) || mediaSourceManager.GetLiveStreamInfo(id) == null)
            {
                continue;
            }

            _logger.LogInformation("Xtream STRM: kanal degisiyor ({Old} -> {New}), cihazin onceki canli yayini kapatiliyor.", name, newName);
            await mediaSourceManager.CloseLiveStream(id).ConfigureAwait(false);
        }
    }

    private static string? ReadLiveStreamId(MemoryStream buffer)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            if (doc.RootElement.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sources.EnumerateArray())
                {
                    if (source.TryGetProperty("LiveStreamId", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()))
                    {
                        return id.GetString();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed record OpenedStream(Guid ItemId, string LiveStreamId);
}
