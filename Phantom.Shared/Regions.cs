using System.Globalization;

namespace Phantom.Shared;

/// <summary>
/// Names for the language and country codes titles carry (TMDB's): ISO 639-1 languages and ISO 3166-1 countries. The
/// common ones are named here, so the names don't depend on what the system's culture data has; the rest come from it.
/// </summary>
public static class Regions
{
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["zh"] = "Chinese (Mandarin)", ["cn"] = "Chinese (Cantonese)", ["ru"] = "Russian",
        ["uk"] = "Ukrainian", ["ja"] = "Japanese", ["ko"] = "Korean", ["hi"] = "Hindi", ["ta"] = "Tamil", ["te"] = "Telugu",
        ["ml"] = "Malayalam", ["bn"] = "Bengali", ["fr"] = "French", ["es"] = "Spanish", ["pt"] = "Portuguese",
        ["it"] = "Italian", ["de"] = "German", ["nl"] = "Dutch", ["sv"] = "Swedish", ["da"] = "Danish", ["no"] = "Norwegian",
        ["nb"] = "Norwegian", ["fi"] = "Finnish", ["pl"] = "Polish", ["cs"] = "Czech", ["hu"] = "Hungarian", ["ro"] = "Romanian",
        ["tr"] = "Turkish", ["el"] = "Greek", ["he"] = "Hebrew", ["ar"] = "Arabic", ["fa"] = "Persian", ["th"] = "Thai",
        ["vi"] = "Vietnamese", ["id"] = "Indonesian", ["ms"] = "Malay", ["tl"] = "Tagalog", ["sr"] = "Serbian",
        ["hr"] = "Croatian", ["bg"] = "Bulgarian", ["ka"] = "Georgian", ["kk"] = "Kazakh", ["is"] = "Icelandic",
        ["xx"] = "No language",
    };

    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "United States", ["GB"] = "United Kingdom", ["CN"] = "China", ["HK"] = "Hong Kong", ["TW"] = "Taiwan",
        ["RU"] = "Russia", ["SU"] = "Soviet Union", ["UA"] = "Ukraine", ["BY"] = "Belarus", ["KZ"] = "Kazakhstan",
        ["JP"] = "Japan", ["KR"] = "South Korea", ["IN"] = "India", ["FR"] = "France", ["DE"] = "Germany", ["IT"] = "Italy",
        ["ES"] = "Spain", ["CA"] = "Canada", ["AU"] = "Australia", ["NZ"] = "New Zealand", ["IE"] = "Ireland",
        ["MX"] = "Mexico", ["BR"] = "Brazil", ["AR"] = "Argentina", ["SE"] = "Sweden", ["DK"] = "Denmark", ["NO"] = "Norway",
        ["FI"] = "Finland", ["PL"] = "Poland", ["TR"] = "Turkey", ["TH"] = "Thailand", ["ID"] = "Indonesia",
        ["PH"] = "Philippines", ["IR"] = "Iran", ["IL"] = "Israel", ["EG"] = "Egypt", ["ZA"] = "South Africa",
        ["NL"] = "Netherlands", ["BE"] = "Belgium", ["CH"] = "Switzerland", ["AT"] = "Austria", ["CZ"] = "Czech Republic",
        ["XC"] = "Czechoslovakia", ["XG"] = "East Germany", ["YU"] = "Yugoslavia",
    };

    public static string Language(string code)
    {
        if (LanguageNames.TryGetValue(code, out var name)) return name;
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            if (!string.IsNullOrEmpty(culture.EnglishName) && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal))
                return culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
        }
        return code.ToUpperInvariant();
    }

    public static string Country(string code)
    {
        if (CountryNames.TryGetValue(code, out var name)) return name;
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code.ToUpperInvariant();
        }
    }

    /// <summary>The codes as choices, by name.</summary>
    public static List<FilterChoice> Languages(IEnumerable<string> codes) =>
        [.. codes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => new FilterChoice(c, Language(c))).OrderBy(c => c.Name, StringComparer.CurrentCulture)];

    /// <summary>The codes (each possibly a comma-separated list) as choices, by name.</summary>
    public static List<FilterChoice> Countries(IEnumerable<string?> lists) =>
        [.. lists.OfType<string>().SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(c => c.ToUpperInvariant()).Distinct()
            .Select(c => new FilterChoice(c, Country(c))).OrderBy(c => c.Name, StringComparer.CurrentCulture)];
}
