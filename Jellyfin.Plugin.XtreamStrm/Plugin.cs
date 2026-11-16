using Jellyfin.Plugin.XtreamStrm.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.XtreamStrm;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Xtream STRM";

    public override Guid Id => Guid.Parse("7b1f6c3e-52a4-4d0e-9a65-3c8e0f4d2b71");

    public override string Description =>
        "Xtream hesabindaki film ve dizileri .strm kisayollari olarak medya klasorlerine yazar.";

    /// <summary>Eklentinin olusturdugu dosyalarin kaydi.</summary>
    public string ManifestPath => Path.Combine(DataFolderPath, "manifest.json");

    /// <summary>Canli TV kanal listesi (Jellyfin M3U tuner'i bu dosyayi okur).</summary>
    public string LiveM3uPath => Path.Combine(DataFolderPath, "live.m3u");

    /// <summary>Secili kanallara suzulmus yayin akisi (XMLTV).</summary>
    public string LiveEpgPath => Path.Combine(DataFolderPath, "epg.xml");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "XtreamStrm",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
