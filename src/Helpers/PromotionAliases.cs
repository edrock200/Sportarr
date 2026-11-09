namespace Sportarr.Api.Helpers;

/// <summary>
/// Full names of combat promotions whose leagues are stored under their
/// abbreviation (and the reverse). TheSportsDB names the league "AEW", while
/// TheTVDB, Sonarr and most libraries name the series "All Elite Wrestling",
/// so a file that spells the name out otherwise names no league at all.
/// Only distinctive multi-word names are listed: a bare "Impact" or "One"
/// would name half the catalogue.
/// </summary>
public static class PromotionAliases
{
    private static readonly (string Abbreviation, string[] FullNames)[] Map =
    {
        ("AEW", new[] { "All Elite Wrestling" }),
        ("WWE", new[] { "World Wrestling Entertainment", "WWF", "World Wrestling Federation" }),
        ("UFC", new[] { "Ultimate Fighting Championship" }),
        ("BKFC", new[] { "Bare Knuckle Fighting Championship" }),
        ("PFL", new[] { "Professional Fighters League" }),
        ("NJPW", new[] { "New Japan Pro Wrestling", "New Japan Pro-Wrestling" }),
        ("ROH", new[] { "Ring of Honor" }),
        ("TNA", new[] { "Total Nonstop Action", "TNA Wrestling", "Impact Wrestling" }),
        ("NWA", new[] { "National Wrestling Alliance" }),
        ("MLW", new[] { "Major League Wrestling" }),
    };

    /// <summary>
    /// Other names for a league called <paramref name="leagueName"/>: the
    /// full names when it is stored as an abbreviation, the abbreviation (and
    /// the other full names) when it is stored spelled out. Empty for every
    /// other league.
    /// </summary>
    public static IEnumerable<string> For(string? leagueName)
    {
        if (string.IsNullOrWhiteSpace(leagueName))
            yield break;

        var name = leagueName.Trim();
        foreach (var (abbreviation, fullNames) in Map)
        {
            var isAbbreviation = name.Equals(abbreviation, StringComparison.OrdinalIgnoreCase);
            var isFullName = fullNames.Any(f => name.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (!isAbbreviation && !isFullName)
                continue;

            if (!isAbbreviation)
                yield return abbreviation;
            foreach (var fullName in fullNames)
            {
                if (!name.Equals(fullName, StringComparison.OrdinalIgnoreCase))
                    yield return fullName;
            }
            yield break;
        }
    }
}
