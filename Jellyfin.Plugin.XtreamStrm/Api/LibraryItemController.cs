using System.Globalization;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XtreamStrm.Api;

/// <summary>
/// "Xtream Filmler" / "Xtream Diziler" kutusundaki bir ogenin kutuphanedeki karsiligini bulur
/// (kumanda betigi film/dizi kartini acar; afis, ozet ve izlenme durumu kutuphanedeki gibi olur).
/// </summary>
[ApiController]
[Route("XtreamStrm")]
[Authorize]
public class LibraryItemController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;

    public LibraryItemController(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    [HttpGet("LibraryItem/{itemId}")]
    public ActionResult Get(Guid itemId)
    {
        var plugin = Plugin.Instance;
        var ext = _libraryManager.GetItemById(itemId)?.ExternalId;
        if (plugin == null || string.IsNullOrEmpty(ext))
        {
            return Ok(new { id = (string?)null });
        }

        var (path, isFolder) = Resolve(plugin, ext);
        var item = path != null ? _libraryManager.FindByPath(path, isFolder) : null;
        return Ok(new { id = item?.Id.ToString("N") });
    }

    private static (string? Path, bool IsFolder) Resolve(Plugin plugin, string ext)
    {
        // Film: "vod_{xtreamId}_{uzanti}"
        if (ext.StartsWith("vod_", StringComparison.Ordinal))
        {
            var id = ext.Split('_')[1];
            var vod = CatalogStore.Load<VodCatalog>(plugin.VodCatalogPath);
            var m = vod.Categories.SelectMany(c => c.Items).FirstOrDefault(i => i.Id == id && i.Path != null);
            return (m?.Path, false);
        }

        var series = CatalogStore.Load<SeriesCatalog>(plugin.SeriesCatalogPath);

        // Bolum: "ep_{xtreamId}_{uzanti}"
        if (ext.StartsWith("ep_", StringComparison.Ordinal))
        {
            var id = ext.Split('_')[1];
            foreach (var s in series.Categories.SelectMany(c => c.Series))
            {
                var e = s.Episodes.FirstOrDefault(x => x.Id == id);
                if (e != null)
                {
                    return (s.EpisodePath(e.S, e.E), false);
                }
            }

            return (null, false);
        }

        // Dizi "sr_..." ya da sezon "ss_...-{n}" klasoru
        foreach (var c in series.Categories)
        {
            foreach (var s in c.Series)
            {
                if (s.Dir == null)
                {
                    continue;
                }

                if (ext == XtreamSeriesChannel.SeriesId(c, s))
                {
                    return (s.Dir, true);
                }

                var prefix = XtreamSeriesChannel.SeasonId(c, s, 0)[..^1];
                if (ext.StartsWith(prefix, StringComparison.Ordinal)
                    && int.TryParse(ext[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var season))
                {
                    return (Path.Combine(s.Dir, $"Season {season:00}"), true);
                }
            }
        }

        return (null, false);
    }
}
