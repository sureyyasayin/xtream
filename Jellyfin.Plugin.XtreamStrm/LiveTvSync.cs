using System.Text;
using System.Text.Json;
using System.Xml;
using Jellyfin.Plugin.XtreamStrm.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// Xtream canli kanallarini bir M3U dosyasina yazar ve bu dosyayi Jellyfin Canli TV'ye
/// tuner olarak kaydeder. Yayin akisi (EPG) secili kanallara suzulup yerel dosyaya yazilir.
/// </summary>
public class LiveTvSync
{
    private const string TunerId = "7b1f6c3e52a44d0e9a653c8e0f4d2b71";
    private const string ListingsId = "7b1f6c3e52a44d0e9a653c8e0f4d2b72";

    private readonly IConfigurationManager _config;
    private readonly ILogger _logger;

    public LiveTvSync(IConfigurationManager config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <returns>Kanal sayisi ve Jellyfin rehberinin yenilenmesi gerekip gerekmedigi.</returns>
    public async Task<(int Channels, bool Changed)> RunAsync(XtreamClient client, PluginConfiguration cfg, Plugin plugin, CancellationToken ct)
    {
        var m3uPath = plugin.LiveM3uPath;
        var epgPath = plugin.LiveEpgPath;

        if (!cfg.SyncLive || cfg.LiveCategoryIds.Length == 0)
        {
            var removed = UpdateLiveTvConfig(null, null, cfg);
            TryDelete(m3uPath);
            TryDelete(epgPath);
            return (0, removed);
        }

        var catNames = new Dictionary<string, string>();
        using (var cats = await client.GetAsync("get_live_categories", null, ct).ConfigureAwait(false))
        {
            if (cats.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in cats.RootElement.EnumerateArray())
                {
                    var id = XtreamClient.Str(el, "category_id");
                    if (id != null)
                    {
                        catNames[id] = XtreamClient.Str(el, "category_name") ?? id;
                    }
                }
            }
        }

        var ext = string.Equals(cfg.LiveFormat, "m3u8", StringComparison.OrdinalIgnoreCase) ? "m3u8" : "ts";
        var sb = new StringBuilder("#EXTM3U\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var epgIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        foreach (var cat in cfg.LiveCategoryIds)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = await client.GetAsync("get_live_streams", "&category_id=" + Uri.EscapeDataString(cat), ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var group = catNames.TryGetValue(cat, out var g) ? g : cat;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = XtreamClient.Str(el, "stream_id");
                var name = XtreamClient.Str(el, "name");
                if (id == null || name == null || !seen.Add(id))
                {
                    continue;
                }

                var epg = XtreamClient.Str(el, "epg_channel_id");
                var icon = XtreamClient.Str(el, "stream_icon");

                sb.Append("#EXTINF:-1");
                if (epg != null)
                {
                    epgIds.Add(epg);
                    sb.Append(" tvg-id=\"").Append(Attr(epg)).Append('"');
                }

                sb.Append(" tvg-name=\"").Append(Attr(name)).Append('"');
                if (icon != null)
                {
                    sb.Append(" tvg-logo=\"").Append(Attr(icon)).Append('"');
                }

                sb.Append(" group-title=\"").Append(Attr(group)).Append('"');
                sb.Append(',').Append(OneLine(name)).Append('\n');
                sb.Append(client.LiveUrl(id, ext)).Append('\n');
                count++;
            }
        }

        var changed = false;
        var content = sb.ToString();
        if (!File.Exists(m3uPath) || File.ReadAllText(m3uPath) != content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m3uPath)!);
            File.WriteAllText(m3uPath, content, new UTF8Encoding(false));
            changed = true;
        }

        if (cfg.LiveUseEpg && epgIds.Count > 0)
        {
            try
            {
                var programmes = await BuildEpgAsync(client, epgIds, epgPath, ct).ConfigureAwait(false);
                _logger.LogInformation("Xtream STRM: yayin akisi guncellendi ({Count} program).", programmes);
                changed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // EPG alinamazsa kanallar yine calisir; eski akis dosyasi korunur.
                _logger.LogWarning("Xtream STRM: yayin akisi (EPG) alinamadi: {Error}", ex.Message);
                TryDelete(epgPath + ".tmp");
            }
        }
        else
        {
            TryDelete(epgPath);
        }

        if (UpdateLiveTvConfig(m3uPath, File.Exists(epgPath) ? epgPath : null, cfg))
        {
            changed = true;
        }

        return (count, changed);
    }

    /// <summary>XMLTV'yi akis halinde okuyup yalnizca secili kanallari yerel dosyaya yazar.</summary>
    private static async Task<int> BuildEpgAsync(XtreamClient client, HashSet<string> ids, string epgPath, CancellationToken ct)
    {
        var tmp = epgPath + ".tmp";
        var programmes = 0;

        using (var resp = await client.OpenXmltvAsync(ct).ConfigureAwait(false))
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            await Task.Run(
                () =>
                {
                    var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, XmlResolver = null };
                    var writerSettings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
                    using var reader = XmlReader.Create(stream, readerSettings);
                    using var writer = XmlWriter.Create(tmp, writerSettings);

                    writer.WriteStartDocument();
                    writer.WriteStartElement("tv");
                    reader.MoveToContent();
                    reader.Read();

                    var n = 0;
                    while (!reader.EOF)
                    {
                        if ((++n & 1023) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                        }

                        if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                        {
                            var isProgramme = reader.Name == "programme";
                            var key = isProgramme ? reader.GetAttribute("channel") : reader.Name == "channel" ? reader.GetAttribute("id") : null;
                            if (key != null && ids.Contains(key))
                            {
                                writer.WriteNode(reader, false);
                                if (isProgramme)
                                {
                                    programmes++;
                                }
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                        else
                        {
                            reader.Read();
                        }
                    }

                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                },
                ct).ConfigureAwait(false);
        }

        File.Move(tmp, epgPath, true);
        return programmes;
    }

    /// <summary>Jellyfin Canli TV ayarlarina tuner ve rehber kaynagini ekler/gunceller/kaldirir.</summary>
    private bool UpdateLiveTvConfig(string? m3uPath, string? epgPath, PluginConfiguration cfg)
    {
        if (_config.GetConfiguration("livetv") is not LiveTvOptions options)
        {
            _logger.LogWarning("Xtream STRM: Canli TV ayarlari okunamadi, tuner kaydedilemedi.");
            return false;
        }

        var changed = false;
        var hosts = (options.TunerHosts ?? Array.Empty<TunerHostInfo>()).ToList();
        var host = hosts.FirstOrDefault(h => h.Id == TunerId);

        if (m3uPath == null)
        {
            if (host != null)
            {
                hosts.Remove(host);
                changed = true;
            }
        }
        else if (host == null)
        {
            hosts.Add(new TunerHostInfo
            {
                Id = TunerId,
                Type = "m3u",
                Url = m3uPath,
                FriendlyName = "Xtream",
                UserAgent = XtreamClient.UserAgent,
                TunerCount = Math.Max(0, cfg.LiveTunerCount),
                ImportFavoritesOnly = false,
                AllowHWTranscoding = true,
                AllowStreamSharing = true,
                EnableStreamLooping = false
            });
            changed = true;
        }
        else if (host.Url != m3uPath || host.TunerCount != Math.Max(0, cfg.LiveTunerCount))
        {
            host.Url = m3uPath;
            host.TunerCount = Math.Max(0, cfg.LiveTunerCount);
            changed = true;
        }

        var providers = (options.ListingProviders ?? Array.Empty<ListingsProviderInfo>()).ToList();
        var provider = providers.FirstOrDefault(p => p.Id == ListingsId);

        if (m3uPath == null || epgPath == null)
        {
            if (provider != null)
            {
                providers.Remove(provider);
                changed = true;
            }
        }
        else if (provider == null)
        {
            providers.Add(new ListingsProviderInfo
            {
                Id = ListingsId,
                Type = "xmltv",
                Path = epgPath,
                EnableAllTuners = false,
                EnabledTuners = new[] { TunerId }
            });
            changed = true;
        }
        else if (provider.Path != epgPath)
        {
            provider.Path = epgPath;
            changed = true;
        }

        if (changed)
        {
            options.TunerHosts = hosts.ToArray();
            options.ListingProviders = providers.ToArray();
            _config.SaveConfiguration("livetv", options);
        }

        return changed;
    }

    private static string Attr(string value) => OneLine(value).Replace('"', '\'');

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
