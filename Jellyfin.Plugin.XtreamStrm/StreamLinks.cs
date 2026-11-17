using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.XtreamStrm.Configuration;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// .strm dosyalarina yazilacak adresleri uretir.
///
/// Sifre gizleme acikken dosyaya Xtream adresi yerine eklentinin imzali adresi yazilir:
/// <c>{sunucu}/XtreamStrm/Play/{imza}/{movie|series}/{id}.{uzanti}</c>. Oynatma aninda eklenti
/// asil adrese yonlendirir (ya da yayini kendisi aktarir). Boylece kullanici adi ve sifre
/// dosyalarda, Jellyfin arayuzunde ve kutuphane bilgilerinde gorunmez; hesap degisince
/// dosyalarin yeniden yazilmasi da gerekmez.
/// </summary>
public sealed partial class StreamLinks
{
    private readonly XtreamClient _client;
    private readonly string? _serverUrl;
    private readonly byte[]? _secret;

    public StreamLinks(XtreamClient client, PluginConfiguration cfg)
    {
        _client = client;
        if (cfg.HideCredentials && !string.IsNullOrEmpty(cfg.StreamSecret))
        {
            _serverUrl = ResolveServerUrl(cfg);
            _secret = Encoding.UTF8.GetBytes(cfg.StreamSecret);
        }
    }

    /// <summary>Sifre gizleme etkin mi (sunucu adresi bilinmiyorsa dogrudan adres yazilir).</summary>
    public bool Hidden => _serverUrl != null && _secret != null;

    [GeneratedRegex(@"/(movie|series)/[^/]+/[^/]+/(\d+)\.([A-Za-z0-9]{1,5})$", RegexOptions.IgnoreCase)]
    private static partial Regex XtreamPath();

    public string Movie(string id, string ext) =>
        Hidden ? Build("movie", id, ext) : _client.MovieUrl(id, ext);

    public string Episode(string id, string ext) =>
        Hidden ? Build("series", id, ext) : _client.EpisodeUrl(id, ext);

    public string Live(string id, string ext) =>
        Hidden ? Build("live", id, ext) : _client.LiveUrl(id, ext);

    /// <summary>M3U listesindeki bolum adresini (sifreli) donusturur.</summary>
    public string FromM3u(string url)
    {
        if (!Hidden)
        {
            return url;
        }

        var m = XtreamPath().Match(url);
        return m.Success ? Build(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value, m.Groups[3].Value) : url;
    }

    private string Build(string kind, string id, string ext) =>
        $"{_serverUrl}/XtreamStrm/Play/{Sign(_secret!, kind, id, ext)}/{kind}/{id}.{ext}";

    public static string Sign(byte[] secret, string kind, string id, string ext)
    {
        var mac = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{kind}/{id}.{ext}"));
        return Convert.ToHexString(mac, 0, 12).ToLowerInvariant();
    }

    public static bool Verify(string secret, string signature, string kind, string id, string ext)
    {
        if (string.IsNullOrEmpty(secret) || signature.Length != 24)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(Encoding.UTF8.GetBytes(secret), kind, id, ext));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
    }

    /// <summary>Ayarlardaki adres, yoksa Jellyfin'in yayinlanan adresi (JELLYFIN_PublishedServerUrl).</summary>
    public static string? ResolveServerUrl(PluginConfiguration cfg)
    {
        foreach (var candidate in new[] { cfg.ServerUrl, Environment.GetEnvironmentVariable("JELLYFIN_PublishedServerUrl") })
        {
            var u = (candidate ?? string.Empty).Trim().TrimEnd('/');
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return u;
            }
        }

        return null;
    }

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
