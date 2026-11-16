using System.Text.Json;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// Eklentinin olusturdugu dosya ve klasorlerin kaydi. Temizlik yalnizca burada
/// kayitli olanlara dokunur; kullanicinin kendi medya dosyalari asla silinmez.
/// </summary>
public class Manifest
{
    public List<string> Files { get; set; } = new();

    public List<string> Dirs { get; set; } = new();

    /// <summary>series_id -> last_modified (degismeyen diziler tekrar sorgulanmaz).</summary>
    public Dictionary<string, string> SeriesStamp { get; set; } = new();

    public Dictionary<string, List<string>> SeriesFiles { get; set; } = new();

    public DateTime? LastRun { get; set; }

    public string LastMessage { get; set; } = string.Empty;

    public static Manifest Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) ?? new Manifest();
            }
        }
        catch (Exception)
        {
            // Bozuk kayit: bos baslanir, mevcut klasorler "kullaniciya ait" sayilip dokunulmaz.
        }

        return new Manifest();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, path, true);
    }
}
