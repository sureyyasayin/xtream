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

    public XtreamClient(HttpClient http, PluginConfiguration config)
    {
        _http = http;
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
    /// Gecici hatalarda (429/5xx, baglanti kopmasi) birkac kez yeniden dener. 4xx yanitlari
    /// (ozellikle 404) sunucunun istegi reddettigini gosterir; israrla tekrarlamak hesabin
    /// engellenmesine yol acabildigi icin yeniden denenmez.
    /// </summary>
    public async Task<JsonDocument> GetAsync(string? action, string? extraQuery, CancellationToken ct)
    {
        var delays = new[] { 2, 6 };
        for (var attempt = 0; ; attempt++)
        {
            // Istekler arasinda kisa bir nefes: sunucunun flood korumasini tetiklememek icin.
            await _pace.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = _nextRequest - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                _nextRequest = DateTime.UtcNow.AddMilliseconds(300);
            }
            finally
            {
                _pace.Release();
            }

            try
            {
                return await GetOnceAsync(action, extraQuery, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < delays.Length && IsTransient(ex, ct))
            {
                await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), ct).ConfigureAwait(false);
            }
        }
    }

    private readonly SemaphoreSlim _pace = new(1, 1);
    private DateTime _nextRequest = DateTime.MinValue;

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ex is HttpRequestException http)
        {
            var code = (int?)http.StatusCode;
            return code is null or 429 or >= 500;
        }

        return ex is IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested);
    }

    private async Task<JsonDocument> GetOnceAsync(string? action, string? extraQuery, CancellationToken ct)
    {
        var url = $"{_base}/player_api.php?username={Uri.EscapeDataString(_user)}&password={Uri.EscapeDataString(_pass)}";
        if (!string.IsNullOrEmpty(action))
        {
            url += "&action=" + action;
        }

        url += extraQuery ?? string.Empty;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "VLC/3.0.20 LibVLC/3.0.20");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, */*");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // URL sifre icerdigi icin mesajda yalnizca durum kodu verilir.
            throw new HttpRequestException($"Xtream sunucusu HTTP {(int)resp.StatusCode} dondurdu ({action ?? "login"}).", null, resp.StatusCode);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Xtream sunucusu gecerli JSON dondurmedi ({action ?? "login"}).");
        }
    }

    /// <summary>Xtream yayin akisini (XMLTV) akis olarak acar. Yanit cagiran tarafindan kapatilir.</summary>
    public async Task<HttpResponseMessage> OpenXmltvAsync(CancellationToken ct)
    {
        var url = $"{_base}/xmltv.php?username={Uri.EscapeDataString(_user)}&password={Uri.EscapeDataString(_pass)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            resp.Dispose();
            throw new HttpRequestException($"Xtream sunucusu HTTP {code} dondurdu (xmltv).");
        }

        return resp;
    }

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
