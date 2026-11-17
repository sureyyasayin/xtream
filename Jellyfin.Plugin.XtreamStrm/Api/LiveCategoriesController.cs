using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XtreamStrm.Api;

/// <summary>Canli TV kategorileri ve numara araliklari (kumanda betigi kategori gosterir ve atlar).</summary>
[ApiController]
[Route("XtreamStrm")]
[Authorize]
public class LiveCategoriesController : ControllerBase
{
    [HttpGet("LiveCategories")]
    public ActionResult Get()
    {
        var path = Plugin.Instance?.LiveCategoriesPath;
        var json = path != null && System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "[]";
        Response.Headers.CacheControl = "no-cache";
        return Content(json, "application/json; charset=utf-8");
    }
}
