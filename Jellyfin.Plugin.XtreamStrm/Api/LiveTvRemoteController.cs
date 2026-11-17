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
        if (cfg == null || !(cfg.LiveRemoteControl || cfg.OpenLibraryCard))
        {
            return Content(string.Empty, "application/javascript; charset=utf-8");
        }

        static string B(bool v) => v ? "true" : "false";
        var settings = "window.__xtreamLiveRemoteCfg={remote:" + B(cfg.LiveRemoteControl)
            + ",arrows:" + B(cfg.LiveRemoteArrows)
            + ",libraryCard:" + B(cfg.OpenLibraryCard) + "};\n";
        return Content(settings + Script.Value, "application/javascript; charset=utf-8");
    }
}
