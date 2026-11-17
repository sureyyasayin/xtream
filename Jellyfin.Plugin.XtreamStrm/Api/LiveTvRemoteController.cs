using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XtreamStrm.Api;

/// <summary>Web istemcisine eklenen canli TV kumanda betigini sunar (oturum acilmadan da yuklenir).</summary>
[ApiController]
[Route("XtreamStrm")]
[AllowAnonymous]
public class LiveTvRemoteController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.XtreamStrm.Web.LiveTvRemote.js";

    private static readonly Lazy<string> Script = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(ResourceName + " bulunamadi.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [HttpGet("LiveTvRemote.js")]
    public ActionResult Get()
    {
        Response.Headers.CacheControl = "no-cache";
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null || !cfg.LiveRemoteControl)
        {
            return Content(string.Empty, "application/javascript; charset=utf-8");
        }

        var settings = "window.__xtreamLiveRemoteCfg={arrows:" + (cfg.LiveRemoteArrows ? "true" : "false") + "};\n";
        return Content(settings + Script.Value, "application/javascript; charset=utf-8");
    }
}
