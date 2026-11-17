using System.Text.Json;
using Jellyfin.Plugin.XtreamStrm.Configuration;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>Xtream Codes player_api.php istemcisi.</summary>
public sealed class XtreamClient
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _user;
    private readonly string _pass;

    private readonly int _delayMs;
    private readonly bool _interactive;

    /// <param name="interactive">Ayar sayfasindan yapilan anlik sorgu: kisa aralik, uzun bekleme yok.</param>
    public XtreamClient(HttpClient http, PluginConfiguration config, bool interactive = false)
    {
        _http = http;
        _interactive = interactive;
        _delayMs = interactive ? 300 : Math.Clamp(config.RequestDelayMs, 300, 60000);
        _base = NormalizeBaseUrl(config.BaseUrl);
        _user = (config.Username ?? string.Empty).Trim();
        _pass = (config.Password ?? string.Empty).Trim();
    }

    public bool IsConfigured => _base.Length > 0 && _user.Length > 0 && _pass.Length > 0;

    /// <summary>Kullanici tam adres (player_api.php / get.php) yapistirsa bile sunucu kokunu cikarir.</summary>
    public static string NormalizeBaseUrl(string? url)
    {
        var u = (url ?? string.Empty).Trim();
        if (u.Length == 0)
        {
            return string.Empty;
        }

        if (!u.Contains("://", StringComparison.Ordinal))
        {
            u = "http://" + u;
        }

        var q = u.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            u = u[..q];
        }

        foreach (var tail in new[] { "/player_api.php", "/get.php", "/xmltv.php", "/panel_api.php" })
        {
            if (u.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                u = u[..^tail.Length];
            }
        }

        return u.TrimEnd('/');
    }

    /// <summary>
    /// Tum istekler buradan gecer: sunucunun hiz sinirina gore araliklarla gonderilir.
    /// Bu tur saglayicilar dakikada ~30 istekten sonra bir sure bos HTTP 404 dondurur;
    /// 404/429 gelince 60 sn, sonra 150 sn bekleyip yeniden denenir. Ust uste 5 istek
    /// basarisiz olursa sunucuyu daha fazla yoklamamak icin kalan istekler gonderilmez.
    /// </summary>
    private async Task<T> SendAsync<T>(Func<Task<T>> once, CancellationToken ct)
    {
        if (Blocked)
        {
            throw new HttpRequestException("Xtream sunucusu istekleri engelliyor; bu tur icin durduruldu.");
        }

        for (var attempt = 0; ; attempt++)
        {
            await _pace.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = _nextRequest - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                _nextRequest = DateTime.UtcNow.AddMilliseconds(_delayMs);
            }
            finally
            {
                _pace.Release();
            }

            try
            {
                var result = await once().ConfigureAwait(false);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var pause = RetryPause(ex, attempt);
                if (pause == null)
                {
                    Interlocked.Increment(ref _consecutiveFailures);
                    throw;
                }

                // Bekleme tum isteklere uygulanir (paralel calisanlar da durur).
                await _pace.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var until = DateTime.UtcNow + pause.Value;
                    if (until > _nextRequest)
                    {
                        _nextRequest = until;
                    }
                }
                finally
                {
                    _pace.Release();
                }
            }
        }
    }

    private readonly SemaphoreSlim _pace = new(1, 1);
    private DateTime _nextRequest = DateTime.MinValue;
    private int _consecutiveFailures;

    /// <summary>Sunucu ust uste istekleri reddediyor; kalan istekler gonderilmez.</summary>
    public bool Blocked => Volatile.Read(ref _consecutiveFailures) >= 5;

    private TimeSpan? RetryPause(Exception ex, int attempt)
    {
        if (ex is HttpRequestException http && (int?)http.StatusCode is int code)
        {
            if (code is 404 or 429)
            {
                // Hiz siniri: ayar sayfasindan yapilan anlik sorgularda bekletme.
                return _interactive ? null : attempt switch { 0 => TimeSpan.FromSeconds(60), 1 => TimeSpan.FromSeconds(150), _ => null };
            }

            if (code < 500)
            {
                return null;
            }
        }
        else if (ex is not (HttpRequestException or IOException or TaskCanceledException))
        {
            return null;
        }

        return attempt switch { 0 => TimeSpan.FromSeconds(3), 1 => TimeSpan.FromSeconds(10), _ => null };
    }

    private string Credentials => $"username={Uri.EscapeDataString(_user)}&password={Uri.EscapeDataString(_pass)}";

    /// <summary>player_api.php sorgusu; yanit JSON olarak dondurulur.</summary>
    public Task<JsonDocument> GetAsync(string? action, string? extraQuery, CancellationToken ct) =>
        SendAsync(() => GetOnceAsync(action, extraQuery, ct), ct);

    private async Task<JsonDocument> GetOnceAsync(string? action, string? extraQuery, CancellationToken ct)
    {
        var label = action ?? "login";
        var query = (string.IsNullOrEmpty(action) ? string.Empty : "&action=" + action) + (extraQuery ?? string.Empty);
        using var resp = await OpenOnceAsync("/player_api.php", query, label, "application/json, */*", ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Xtream sunucusu gecerli JSON dondurmedi ({label}).");
        }
    }

    /// <summary>Yaniti akis olarak acar; cagiran taraf kapatir.</summary>
    private async Task<HttpResponseMessage> OpenOnceAsync(string path, string query, string label, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}{path}?{Credentials}{query}");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", accept);

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // URL sifre icerdigi icin mesajda yalnizca durum kodu verilir.
            var status = resp.StatusCode;
            resp.Dispose();
            throw new HttpRequestException($"Xtream sunucusu HTTP {(int)status} dondurdu ({label}).", null, status);
        }

        return resp;
    }

    private Task<HttpResponseMessage> OpenRawAsync(string path, string query, string label, CancellationToken ct) =>
        SendAsync(() => OpenOnceAsync(path, query, label, "*/*", ct), ct);

    /// <summary>Xtream yayin akisini (XMLTV) akis olarak acar. Yanit cagiran tarafindan kapatilir.</summary>
    public Task<HttpResponseMessage> OpenXmltvAsync(CancellationToken ct) =>
        OpenRawAsync("/xmltv.php", string.Empty, "xmltv", ct);

    /// <summary>Tum icerigi (kanal, film, dizi bolumleri) iceren M3U listesini akis olarak acar.</summary>
    public Task<HttpResponseMessage> OpenM3uAsync(CancellationToken ct) =>
        OpenRawAsync("/get.php", "&type=m3u_plus&output=ts", "m3u", ct);

    public const string UserAgent = "VLC/3.0.20 LibVLC/3.0.20";

    public string LiveUrl(string streamId, string ext) =>
        $"{_base}/live/{Uri.EscapeDataString(_user)}/{Uri.EscapeDataString(_pass)}/{streamId}.{ext}";

    public string MovieUrl(string streamId, string ext) =>
        $"{_base}/movie/{Uri.EscapeDataString(_user)}/{Uri.EscapeDataString(_pass)}/{streamId}.{ext}";

    public string EpisodeUrl(string episodeId, string ext) =>
        $"{_base}/series/{Uri.EscapeDataString(_user)}/{Uri.EscapeDataString(_pass)}/{episodeId}.{ext}";

    /// <summary>Alan string de sayi da olabilir; ikisini de metne cevirir.</summary>
    public static string? Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
        {
            return null;
        }

        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };

        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
