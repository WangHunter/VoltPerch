using System.Text.Json;
using System.Text.RegularExpressions;
using BatteryDesk;

try
{
    var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
    var source = Path.Combine(root, "src", "BatteryDesk");
    var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(source, "Localization", "en.json")))!;
    var zh = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(source, "Localization", "zh-CN.json")))!;
    int count = 0;
    void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); count++; }
    Check(en.Keys.ToHashSet().SetEquals(zh.Keys), "Language resource keys differ");
    foreach (var key in en.Keys)
    {
        string Arguments(string value) => string.Join(",", Regex.Matches(value, @"\{(\d+)(?:[^}]*)\}").Select(m => m.Groups[1].Value).Order());
        Check(Arguments(en[key]) == Arguments(zh[key]), "Format arguments differ: " + key);
        Check(!string.IsNullOrWhiteSpace(zh[key]), "Empty translation: " + key);
    }
    foreach (var path in Directory.EnumerateFiles(source, "*.cs", SearchOption.TopDirectoryOnly))
    {
        foreach (Match match in Regex.Matches(File.ReadAllText(path), "(?:UiHelpers|Localizer)\\.T\\(\"((?:\\\\.|[^\"\\\\])*)\""))
        {
            var key = Regex.Unescape(match.Groups[1].Value);
            Check(en.ContainsKey(key), "Unregistered source string: " + key);
        }
    }
    foreach (var path in Directory.EnumerateFiles(source, "*.xaml"))
        foreach (Match match in Regex.Matches(File.ReadAllText(path), @"DynamicResource (Loc\.\w+)"))
            Check(en.ContainsKey(match.Groups[1].Value), "Missing XAML resource: " + match.Groups[1].Value);
    Check(Localizer.Language == "en" && Localizer.T("Settings") == "Settings", "English is not the default");
    Localizer.SetLanguage("zh-CN");
    Check(Localizer.T("Settings") == zh["Settings"], "Switching language did not update text");
    Check(Localizer.T("Removed devices ({0})", 2).Contains('2'), "Formatted translation lost its argument");
    Check(Localizer.Resources.Single(p => p.Key == "Loc.Settings").Value == zh["Settings"], "Dynamic XAML resources did not switch");
    Localizer.SetLanguage("en");
    Check(Localizer.T("Settings") == "Settings", "Switching back retained stale translated text");
    Check(Localizer.Translate(zh["Bluetooth"]) == "Bluetooth", "Legacy cache display did not migrate");
    Localizer.SetLanguage("unsupported-language");
    Check(Localizer.Language == "en", "Unknown language must fall back to English");
    Console.WriteLine($"PASS: {count} localization checks");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
