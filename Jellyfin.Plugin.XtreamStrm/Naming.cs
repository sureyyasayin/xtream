using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>Xtream isimlerini Jellyfin'in tanidigi klasor/dosya adlarina cevirir.</summary>
public static partial class Naming
{
    [GeneratedRegex(@"^\s*(?:(?:\[[^\]]{1,12}\]|\([A-Z]{2,3}\)|[A-Z0-9]{2,4}\s*[:|])\s*)+")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\((?:19|20)\d{2}\)")]
    private static partial Regex YearParenRegex();

    [GeneratedRegex(@"\s+[-–]\s*((?:19|20)\d{2})\s*$")]
    private static partial Regex YearTailRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();

    /// <returns>Base: "Film (2024)", Folder: "Film (2024) [tmdbid-123]"</returns>
    public static (string Base, string Folder) Build(string rawName, string? year, string? tmdbId, bool stripPrefixes)
    {
        var n = rawName.Trim();
        if (stripPrefixes)
        {
            var stripped = PrefixRegex().Replace(n, string.Empty).Trim();
            if (stripped.Length > 0)
            {
                n = stripped;
            }
        }

        if (!YearParenRegex().IsMatch(n))
        {
            var m = YearTailRegex().Match(n);
            if (m.Success && m.Index > 0)
            {
                n = n[..m.Index].TrimEnd() + " (" + m.Groups[1].Value + ")";
            }
            else
            {
                var y = NormalizeYear(year);
                if (y != null)
                {
                    n += " (" + y + ")";
                }
            }
        }

        var baseName = Sanitize(n);
        if (baseName.Length == 0)
        {
            baseName = "Adsiz";
        }

        var folder = baseName;
        if (long.TryParse(tmdbId, out var id) && id > 0)
        {
            folder += " [tmdbid-" + id + "]";
        }

        return (baseName, folder);
    }

    /// <summary>"2024" veya "2024-05-01" gibi degerlerden yili cikarir.</summary>
    public static string? NormalizeYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 4)
        {
            return null;
        }

        var y = value.Trim()[..4];
        return int.TryParse(y, out var num) && num >= 1900 && num <= DateTime.UtcNow.Year + 1 ? y : null;
    }

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            switch (c)
            {
                case ':':
                    sb.Append(" -");
                    break;
                case '/':
                case '\\':
                    sb.Append('-');
                    break;
                case '*':
                case '?':
                case '"':
                case '<':
                case '>':
                case '|':
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        var s = SpacesRegex().Replace(sb.ToString(), " ").Trim().TrimEnd('.', ' ');
        if (s.Length > 180)
        {
            s = s[..180].TrimEnd('.', ' ');
        }

        return s;
    }
}
