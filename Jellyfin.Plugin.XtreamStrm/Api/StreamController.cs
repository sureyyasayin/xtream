using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.XtreamStrm.Api;

/// <summary>
/// .strm dosyalarindaki imzali adresleri asil Xtream adresine yonlendirir ya da yayini aktarir.
/// Oynaticilar ve ffmpeg oturum bilgisi gondermedigi icin giris istenmez; bunun yerine her
/// adres sunucuya ozel gizli anahtarla imzalidir, imzasiz ya da degistirilmis adres reddedilir.
/// </summary>
[ApiController]
[Route("XtreamStrm/Play")]
[AllowAnonymous]
public partial class StreamController : ControllerBase
{
    private static readonly string[] ForwardedResponseHeaders =
    {
        HeaderNames.ContentType, HeaderNames.ContentLength, HeaderNames.ContentRange,
        HeaderNames.AcceptRanges, HeaderNames.LastModified, HeaderNames.ETag
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StreamController> _logger;

    public StreamController(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, ILogger<StreamController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    [GeneratedRegex(@"^(\d{1,12})\.([A-Za-z0-9]{1,5})$")]
    private static partial Regex FileName();

    [HttpGet("{signature}/{kind}/{file}")]
    [HttpHead("{signature}/{kind}/{file}")]
    public async Task<ActionResult> Play(string signature, string kind, string file)
    {
        var cfg = Plugin.Instance?.Configuration;
        var m = FileName().Match(file);
        if (cfg == null || !m.Success || (kind != "movie" && kind != "series" && kind != "live"))
        {
            return NotFound();
        }

        var id = m.Groups[1].Value;
        var ext = m.Groups[2].Value;
        if (!StreamLinks.Verify(cfg.StreamSecret, signature, kind, id, ext))
        {
            return NotFound();
        }

        var client = new XtreamClient(_httpClientFactory.CreateClient(), cfg);
        if (!client.IsConfigured)
        {
            return NotFound();
        }

        // Film saglayicida bos geliyorsa ayni filmin baska bir kopyasi acilir.
        if (kind == "movie")
        {
            (id, ext) = await new MovieFallback(_httpClientFactory, _libraryManager, _logger).ResolveAsync(client, id, ext, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        var target = kind switch
        {
            "movie" => client.MovieUrl(id, ext),
            "series" => client.EpisodeUrl(id, ext),
            _ => client.LiveUrl(id, ext)
        };
        Response.Headers.CacheControl = "no-store";

        if (!cfg.ProxyStreams)
        {
            return Redirect(target);
        }

        return await ProxyAsync(target).ConfigureAwait(false);
    }

    /// <summary>Yayini sunucu uzerinden aktarir; sifre hicbir oynaticiya ulasmaz.</summary>
    private async Task<ActionResult> ProxyAsync(string target)
    {
        var ct = HttpContext.RequestAborted;
        var http = _httpClientFactory.CreateClient();
        http.Timeout = Timeout.InfiniteTimeSpan;

        using var req = new HttpRequestMessage(HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get, target);
        req.Headers.TryAddWithoutValidation("User-Agent", XtreamClient.UserAgent);
        foreach (var name in new[] { HeaderNames.Range, HeaderNames.IfRange })
        {
            if (Request.Headers.TryGetValue(name, out var value))
            {
                req.Headers.TryAddWithoutValidation(name, value.ToString());
            }
        }

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        using (resp)
        {
            Response.StatusCode = (int)resp.StatusCode;
            foreach (var name in ForwardedResponseHeaders)
            {
                if (resp.Headers.TryGetValues(name, out var values) || resp.Content.Headers.TryGetValues(name, out values))
                {
                    Response.Headers[name] = values.ToArray();
                }
            }

            if (!HttpMethods.IsHead(Request.Method))
            {
                try
                {
                    await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await body.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    // Oynatici baglantiyi kapatti (sarma, durdurma): normal.
                }
            }
        }

        return new EmptyResult();
    }
}
