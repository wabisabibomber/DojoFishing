using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DojoFishing;

public sealed record Settings
{
    public int Version { get; init; } = 2;
    public string ProcessFilter { get; init; } = "VRChat.exe";
    public double Threshold { get; init; } = .55;
    [JsonIgnore] public double ReleaseThreshold => Threshold * .9;
    [JsonIgnore] public double MinimumDb => -80;
    public int IntervalMs { get; init; } = 1800;
    [JsonIgnore] public int ReleaseMs => 200;
    [JsonIgnore] public int ConfirmFrames => 2;
    public string Hotkey { get; init; } = "Ctrl+Alt+F10";

    public void Validate()
    {
        if (Version != 2) throw new InvalidDataException("この作業版の設定ファイルではありません。");
        if (!double.IsFinite(Threshold) || Threshold is < .1 or > 1 ||
            IntervalMs is < 1 or > 600000 || ProcessFilter is null || string.IsNullOrWhiteSpace(Hotkey) ||
            ProcessFilter.Contains('\r') || ProcessFilter.Contains('\n') || Hotkey.Contains('\r') || Hotkey.Contains('\n'))
            throw new InvalidDataException("閾値は0.10～1.00、クリック間隔は1～600000msで指定してください。");
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings LoadCurrent(string? directory = null)
    {
        directory ??= AppContext.BaseDirectory;
        string json = Path.Combine(directory, "settings.json");
        string ini = Path.Combine(directory, "settings.ini");
        // The INI-era build may have left an older JSON beside the latest settings.
        if (File.Exists(ini) && (!File.Exists(json) || File.GetLastWriteTimeUtc(ini) > File.GetLastWriteTimeUtc(json)))
        {
            var migrated = LoadLegacyIni(ini);
            migrated.Save(json);
            return migrated;
        }
        return File.Exists(json) ? Load(json) : new Settings();
    }

    public static Settings Load(string path)
    {
        var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8))
            ?? throw new InvalidDataException("設定ファイルが空です。");
        value.Validate();
        return value;
    }

    private static Settings LoadLegacyIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inSection = false;
        foreach (string raw in File.ReadLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line.Equals("[DojoFishing]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            int separator = line.IndexOf('=');
            if (separator <= 0) throw new InvalidDataException("INI設定の行が不正です。");
            string key = line[..separator].Trim();
            if (!values.TryAdd(key, Unquote(line[(separator + 1)..].Trim())))
                throw new InvalidDataException("INI設定に重複したキーがあります: " + key);
        }
        if (values.Count == 0) throw new InvalidDataException("INIに[DojoFishing]設定がありません。");
        var defaults = new Settings();
        var value = defaults with
        {
            Version = values.TryGetValue("Version", out var version) ? int.Parse(version, CultureInfo.InvariantCulture) : defaults.Version,
            ProcessFilter = values.GetValueOrDefault("ProcessFilter", defaults.ProcessFilter),
            Threshold = values.TryGetValue("Threshold", out var threshold) ? double.Parse(threshold, CultureInfo.InvariantCulture) : defaults.Threshold,
            IntervalMs = values.TryGetValue("IntervalMs", out var interval) ? int.Parse(interval, CultureInfo.InvariantCulture) : defaults.IntervalMs,
            Hotkey = values.GetValueOrDefault("Hotkey", defaults.Hotkey)
        };
        value.Validate();
        return value;
    }

    private static string Unquote(string value)
    {
        if (!value.StartsWith('"')) return value;
        if (value.Length < 2 || !value.EndsWith('"')) throw new InvalidDataException("INI設定の引用符が不正です。");
        return value[1..^1].Replace("\"\"", "\"");
    }

    public void Save(string path)
    {
        Validate();
        string content = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
