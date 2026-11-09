using System.Text.RegularExpressions;

namespace Sportarr.Api.Helpers;

/// <summary>
/// Removes Sonarr/TVDB-style season/episode markers ("S2024E05", "S01E05",
/// "S2024E05-E06") from a file name so a library scan can match the rest of
/// the name against Sportarr's own metadata.
///
/// Media that was named by another tool against TheTVDB carries TVDB's
/// episode numbering, which rarely lines up with Sportarr's: the scanner
/// treats an S/E token as authoritative, so a TVDB "E05" rejects the
/// right event outright and steers the file to whichever event Sportarr
/// numbers 5. With the token gone, matching falls back to the title,
/// date, round and league signals in the rest of the name.
///
/// A four-digit season is a year (TVDB numbers sports seasons by year),
/// and the year is the one piece of the marker that still agrees with
/// Sportarr, so <see cref="Strip(string, out int?)"/> hands it back for the
/// caller to use as the file's year. Without it the scan loses its guard
/// against matching the same fixture from a different season. It is not
/// left in the name as a bare year: the filename parser reads a lone year
/// as a January 1st air date, which the date gate would then hold against
/// every event.
/// </summary>
public static class SeasonEpisodeTokens
{
    private static readonly Regex Marker = new(
        @"(?<![A-Za-z0-9])S(?<season>\d{1,4})[ ._]?E\d{1,4}(?:[ ._-]?E\d{1,4})*(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedDash = new(@"\s*-\s*(?:-\s*)+", RegexOptions.Compiled);
    private static readonly Regex RepeatedDot = new(@"\.(?:\s*\.)+", RegexOptions.Compiled);
    private static readonly Regex RepeatedUnderscore = new(@"_(?:\s*_)+", RegexOptions.Compiled);
    private static readonly Regex RepeatedSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>True when the name carries an S/E marker.</summary>
    public static bool HasMarker(string? name) =>
        !string.IsNullOrEmpty(name) && Marker.IsMatch(name);

    /// <summary>The name with every S/E marker removed.</summary>
    public static string Strip(string name) => Strip(name, out _);

    /// <summary>
    /// The name with every S/E marker removed, and the season of the first
    /// marker when it is a year (1900-2099). A name without a marker is
    /// returned unchanged.
    /// </summary>
    public static string Strip(string name, out int? seasonYear)
    {
        seasonYear = null;
        if (string.IsNullOrEmpty(name))
            return name;

        var first = Marker.Match(name);
        if (!first.Success)
            return name;

        var season = first.Groups["season"].Value;
        if (season.Length == 4 && int.TryParse(season, out var year) && year is >= 1900 and <= 2099)
            seasonYear = year;

        var stripped = Marker.Replace(name, string.Empty);
        stripped = RepeatedDash.Replace(stripped, " - ");
        stripped = RepeatedDot.Replace(stripped, ".");
        stripped = RepeatedUnderscore.Replace(stripped, "_");
        stripped = RepeatedSpace.Replace(stripped, " ");
        stripped = stripped.Trim(' ', '-', '.', '_');

        // A name that was nothing but the marker keeps its original text
        // rather than becoming empty.
        return string.IsNullOrWhiteSpace(stripped) ? name : stripped;
    }
}
