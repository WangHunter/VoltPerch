using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace BatteryDesk;

/// <summary>English source strings with embedded, optional UI translations.</summary>
internal static class Localizer
{
    private static readonly IReadOnlyDictionary<string, string> English = Read("en");
    private static readonly IReadOnlyDictionary<string, string> Chinese = Read("zh-CN");
    private static readonly KeyValuePair<string, string>[] TranslatedPhrases = Chinese
        .Where(p => !p.Key.StartsWith("Loc.", StringComparison.Ordinal) && p.Key != p.Value && !p.Key.Contains('{'))
        .OrderByDescending(p => p.Key.Length).ToArray();
    private static readonly KeyValuePair<string, string>[] LegacyPhrases = TranslatedPhrases
        .OrderByDescending(p => p.Value.Length).ToArray();
    private static string _language = "en";
    internal static string Language => _language;
    internal static event Action? Changed;

    internal static void SetLanguage(string? language)
    {
        var next = language == "zh-CN" ? "zh-CN" : "en";
        if (_language == next) return;
        _language = next;
        Changed?.Invoke();
    }

    internal static string T(string key, params object[] arguments)
    {
        var strings = _language == "zh-CN" ? Chinese : English;
        var value = strings.TryGetValue(key, out var translated) ? translated : key;
        return arguments.Length == 0 ? value : string.Format(
            CultureInfo.GetCultureInfo(_language == "zh-CN" ? "zh-CN" : "en-US"), value, arguments);
    }

    // Hardware diagnostics stay canonical in persisted state; translate only for display.
    internal static string Translate(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (_language != "zh-CN")
        {
            // Older installations stored display strings in Chinese. Convert those
            // known diagnostics on display without rewriting IDs or battery history.
            foreach (var phrase in LegacyPhrases)
                text = text.Replace(phrase.Value, phrase.Key, StringComparison.Ordinal);
            return text;
        }
        if (Chinese.TryGetValue(text, out var exact)) return exact;
        foreach (var phrase in TranslatedPhrases)
            text = text.Replace(phrase.Key, phrase.Value, StringComparison.Ordinal);
        return text;
    }

    internal static IEnumerable<KeyValuePair<string, string>> Resources => English
        .Where(p => p.Key.StartsWith("Loc.", StringComparison.Ordinal))
        .Select(p => new KeyValuePair<string, string>(p.Key, T(p.Key)));

    private static IReadOnlyDictionary<string, string> Read(string language)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "BatteryDesk.Localization." + language + ".json");
        if (stream == null) return new Dictionary<string, string>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
