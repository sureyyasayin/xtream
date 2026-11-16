using System.Text.Json;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XtreamStrm.Api;

/// <summary>Ayar sayfasinin kullandigi yonetici uclari.</summary>
[ApiController]
[Route("XtreamStrm")]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class XtreamStrmController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITaskManager _taskManager;

    public XtreamStrmController(IHttpClientFactory httpClientFactory, ITaskManager taskManager)
    {
        _httpClientFactory = httpClientFactory;
        _taskManager = taskManager;
    }

    /// <summary>Kayitli bilgilerle Xtream'e baglanmayi dener.</summary>
    [HttpGet("Test")]
    public async Task<ActionResult> Test(CancellationToken ct)
    {
        var client = CreateClient();
        if (!client.IsConfigured)
        {
            return Ok(new { ok = false, message = "Adres, kullanici adi ve sifre girilmeli." });
        }

        try
        {
            using var doc = await client.GetAsync(null, null, ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("user_info", out var ui))
            {
                var auth = XtreamClient.Str(ui, "auth");
                var status = XtreamClient.Str(ui, "status") ?? "?";
                if (auth == "1")
                {
                    var exp = XtreamClient.Str(ui, "exp_date");
                    var expText = long.TryParse(exp, out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd.MM.yyyy")
                        : "suresiz";
                    return Ok(new { ok = true, message = $"Baglanti basarili. Durum: {status}, bitis: {expText}" });
                }

                return Ok(new { ok = false, message = "Kullanici adi veya sifre hatali." });
            }

            return Ok(new { ok = false, message = "Sunucu beklenen yaniti vermedi (Xtream API degil?)." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Ok(new { ok = false, message = "Baglanti hatasi: " + ex.Message });
        }
    }

    /// <summary>Film ve dizi kategorilerini dondurur.</summary>
    [HttpGet("Categories")]
    public async Task<ActionResult> Categories(CancellationToken ct)
    {
        var client = CreateClient();
        if (!client.IsConfigured)
        {
            return Ok(new { ok = false, message = "Adres, kullanici adi ve sifre girilmeli." });
        }

        try
        {
            var movies = await ReadCategories(client, "get_vod_categories", ct).ConfigureAwait(false);
            var series = await ReadCategories(client, "get_series_categories", ct).ConfigureAwait(false);
            var live = await ReadCategories(client, "get_live_categories", ct).ConfigureAwait(false);
            return Ok(new { ok = true, movies, series, live });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Ok(new { ok = false, message = "Kategoriler alinamadi: " + ex.Message });
        }
    }

    /// <summary>Senkronizasyon gorevini baslatir.</summary>
    [HttpPost("Sync")]
    public ActionResult Sync()
    {
        _taskManager.QueueIfNotRunning<XtreamSyncTask>();
        return NoContent();
    }

    /// <summary>Son senkronizasyonun ozeti.</summary>
    [HttpGet("Status")]
    public ActionResult Status()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return Ok(new { lastRun = (DateTime?)null, message = string.Empty, files = 0 });
        }

        var m = Manifest.Load(plugin.ManifestPath);
        return Ok(new { lastRun = m.LastRun, message = m.LastMessage, files = m.Files.Count });
    }

    private XtreamClient CreateClient()
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        return new XtreamClient(http, Plugin.Instance!.Configuration);
    }

    private static async Task<List<object>> ReadCategories(XtreamClient client, string action, CancellationToken ct)
    {
        var list = new List<object>();
        using var doc = await client.GetAsync(action, null, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = XtreamClient.Str(el, "category_id");
                if (id != null)
                {
                    list.Add(new { id, name = XtreamClient.Str(el, "category_name") ?? id });
                }
            }
        }

        return list;
    }
}
