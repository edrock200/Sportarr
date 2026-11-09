using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sportarr.Api.Helpers;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

/// <summary>
/// Date-first matching for numbered weekly shows: the events TheSportsDB
/// titles "&lt;Show&gt; #&lt;N&gt;" ("Collision #33", "RAW #1607", "Collision #85
/// Slam Dunk Saturday"). The general scorer cannot reach the auto-import
/// floor for these: the event title never carries the date a file is named
/// by, so "AEW.Collision.2024.02.03" shares one word with "Collision #33"
/// and tops out in the 60s.
///
/// A file qualifies when it names the event's league (or a promotion alias,
/// <see cref="PromotionAliases"/>), names the show, and carries a full
/// calendar date. Then, among the show's open events:
/// <list type="number">
/// <item>the event carrying the file's own show number within
/// <see cref="NumberToleranceDays"/> of its date is the match;</item>
/// <item>otherwise the show's single airing on (or a day either side of)
/// the file's date is the match. The date decides and the number confirms:
/// Sportarr's running numbers can sit a show or two off the real ones for
/// months, but a number further off than <see cref="DriftMax"/> without the
/// title agreeing is left to the general scorer and a person.</item>
/// </list>
/// Two-night shows under one number ("Night 1"/"Night 2", "Saturday"/
/// "Sunday") and duplicated upstream rows are narrowed by the file's markers,
/// its title words and its day. Anything still ambiguous returns null.
///
/// A file's date is the local calendar date it aired; Sportarr keeps that as
/// <see cref="Event.BroadcastDate"/>, with <see cref="Event.EventDate"/> in
/// UTC (a Saturday 8 PM Eastern show is stamped Sunday 01:00Z). The local
/// date is compared with the broadcast date, or with noon UTC when an event
/// has none.
/// </summary>
internal sealed class ShowEpisodeMatcher
{
    internal const int NumberAndDateConfidence = 95;
    internal const int AirDateConfidence = 90;

    internal const double NumberToleranceDays = 2;
    internal const double AirDateWindowDays = 1.5;
    internal const int DriftMax = 2;

    internal sealed record ShowMatch(Event Event, int Confidence, string Reason);

    private sealed record ShowEvent(
        Event Event,
        string ShowPhrase,
        int Number,
        string FoldedTitle,
        HashSet<string> TitleWords,
        List<(string Kind, int N)> Segments,
        HashSet<string> Weekdays,
        string[] LeagueNames);

    private static readonly Regex ShowTitle = new(
        @"^(?<show>[^#]+?)\s*#\s*(?<num>\d{1,5})(?!\d)(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FileDate = new(
        @"(?<!\d)(?<y>(?:19|20)\d{2})[-._ ](?<m>\d{2})[-._ ](?<d>\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HashNumber = new(
        @"#\s*(?<num>\d{1,5})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SegmentMarker = new(
        @"\b(?<kind>night|day|part|pt)\s*(?<n>\d{1,2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] WeekdayNames =
        { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "at", "for", "in", "of", "on", "the", "to", "vs", "v",
        "night", "day", "part", "pt", "show", "live", "special", "episode",
        "webdl", "web", "dl", "webrip", "hdtv", "bluray", "x264", "x265", "h264", "h265", "hevc",
        "1080p", "720p", "2160p", "480p", "4k", "aac", "proper", "repack",
    };

    private readonly List<ShowEvent> _showEvents = new();

    /// <summary>Index the events that carry a "&lt;Show&gt; #&lt;N&gt;" title.</summary>
    internal ShowEpisodeMatcher(IEnumerable<Event> events)
    {
        foreach (var evt in events)
        {
            if (evt.League == null || string.IsNullOrWhiteSpace(evt.Title))
                continue;
            // Team fixtures are matched by their teams, never by a number.
            if (!string.IsNullOrWhiteSpace(evt.HomeTeamName) || !string.IsNullOrWhiteSpace(evt.AwayTeamName) ||
                evt.HomeTeamId.HasValue || evt.AwayTeamId.HasValue)
                continue;
            var parsed = ShowTitle.Match(evt.Title);
            if (!parsed.Success || !int.TryParse(parsed.Groups["num"].Value, out var number))
                continue;

            var leagueNames = new[] { evt.League.Name }
                .Concat(SplitAlternateNames(evt.League.AlternateName))
                .Concat(PromotionAliases.For(evt.League.Name))
                .Select(Fold)
                .Where(n => n.Length > 0)
                .Distinct()
                .ToArray();

            // "AEW Collision #5" and "Collision #5" are the same show.
            var show = Fold(parsed.Groups["show"].Value);
            foreach (var name in leagueNames)
                show = RemovePhrase(show, name);
            if (show.Length == 0)
                continue;

            var rest = Fold(parsed.Groups["rest"].Value);
            _showEvents.Add(new ShowEvent(
                evt,
                show,
                number,
                Fold(evt.Title),
                DistinctiveWords(rest, show, leagueNames),
                Segments(rest),
                Weekdays(rest),
                leagueNames));
        }
    }

    internal bool IsEmpty => _showEvents.Count == 0;

    /// <summary>
    /// The one open event the file names, or null when the file is not a
    /// dated numbered-show file or more than one event fits.
    /// </summary>
    /// <param name="fileName">File name without extension.</param>
    /// <param name="folderName">Name of the folder holding the file, read
    /// for the league and show names only ("AEW.Collision/2024-02-03.mkv").</param>
    /// <param name="isOpen">Whether an event may still take a file (no file
    /// of its own, not claimed by an earlier file in the batch).</param>
    internal ShowMatch? TryMatch(string fileName, string? folderName, Func<Event, bool> isOpen)
    {
        if (_showEvents.Count == 0 || string.IsNullOrWhiteSpace(fileName))
            return null;

        var fileDate = ParseFileDate(fileName);
        if (fileDate == null)
            return null;

        var foldedFile = Fold(fileName);
        var foldedContext = string.IsNullOrWhiteSpace(folderName)
            ? foldedFile
            : foldedFile + " " + Fold(folderName);

        // The show's open events, for shows the file names in a league the
        // file names. Another promotion's "Collision" stays out.
        // Each distinct show and league name is looked up once per file.
        var named = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Named(string phrase)
        {
            if (!named.TryGetValue(phrase, out var found))
                named[phrase] = found = ContainsPhrase(foldedContext, phrase);
            return found;
        }

        var pool = _showEvents
            .Where(s => Named(s.ShowPhrase) && s.LeagueNames.Any(Named) && isOpen(s.Event))
            .ToList();
        if (pool.Count == 0)
            return null;

        var fileNumber = ParseFileNumber(fileName, foldedFile, pool, fileDate.Value);
        var fileSegments = Segments(foldedFile);
        var fileWeekdays = Weekdays(foldedFile);

        // a. Number + date.
        if (fileNumber.HasValue)
        {
            var numbered = pool
                .Where(s => s.Number == fileNumber.Value &&
                            DaysApart(s.Event, fileDate.Value) <= NumberToleranceDays)
                .ToList();
            var chosen = Narrow(numbered, foldedFile, fileSegments, fileWeekdays, fileDate.Value);
            if (chosen != null)
            {
                return new ShowMatch(chosen.Event, NumberAndDateConfidence,
                    $"show number {fileNumber} aired {fileDate:yyyy-MM-dd}");
            }
        }

        // b. Air date.
        var airing = pool
            .Where(s => WithinAirDateWindow(s.Event, fileDate.Value))
            .ToList();
        var byDate = Narrow(airing, foldedFile, fileSegments, fileWeekdays, fileDate.Value);
        if (byDate == null)
            return null;

        if (fileNumber.HasValue && Math.Abs(byDate.Number - fileNumber.Value) > DriftMax &&
            !TitleWordsAgree(byDate, foldedFile))
        {
            // A different show that happens to share the week: Sportarr's
            // data holds whole blocks dated a year off, and a date match
            // there pairs two unrelated episodes.
            return null;
        }

        var reason = fileNumber.HasValue && fileNumber.Value != byDate.Number
            ? $"aired {fileDate:yyyy-MM-dd}; Sportarr numbers it #{byDate.Number} (file says #{fileNumber})"
            : $"aired {fileDate:yyyy-MM-dd}";
        return new ShowMatch(byDate.Event, AirDateConfidence, reason);
    }

    private static ShowEvent? Narrow(
        List<ShowEvent> candidates, string foldedFile,
        List<(string Kind, int N)> fileSegments, HashSet<string> fileWeekdays, DateTime fileDate)
    {
        if (candidates.Count <= 1)
            return candidates.SingleOrDefault();

        // Markers: keep the candidates that agree with the file's Night/Day/
        // Part number or weekday; failing that, drop those that contradict.
        if (fileSegments.Count > 0 || fileWeekdays.Count > 0)
        {
            var agreeing = candidates.Where(c =>
                c.Segments.Any(fileSegments.Contains) || c.Weekdays.Overlaps(fileWeekdays)).ToList();
            if (agreeing.Count > 0)
            {
                candidates = agreeing;
            }
            else
            {
                candidates = candidates.Where(c =>
                    !c.Segments.Any(seg => fileSegments.Any(f => f.Kind == seg.Kind && f.N != seg.N)) &&
                    !(c.Weekdays.Count > 0 && fileWeekdays.Count > 0 && !c.Weekdays.Overlaps(fileWeekdays)))
                    .ToList();
            }
            if (candidates.Count <= 1)
                return candidates.SingleOrDefault();
        }

        // A unique best title-word overlap ("Slam Dunk", "Holiday Bash").
        var overlaps = candidates
            .Select(c => (Candidate: c, Score: c.TitleWords.Count(w => ContainsPhrase(foldedFile, w))))
            .OrderByDescending(x => x.Score)
            .ToList();
        if (overlaps[0].Score > 0 && overlaps[0].Score > overlaps[1].Score)
            return overlaps[0].Candidate;

        // The same title twice on one day is one upstream event listed
        // twice; the lower running number is the original row.
        if (candidates.Select(c => c.FoldedTitle).Distinct().Count() == 1 &&
            candidates.Select(c => LocalDay(c.Event)).Distinct().Count() == 1)
        {
            return candidates.OrderBy(c => c.Event.EpisodeNumber ?? int.MaxValue).ThenBy(c => c.Event.Id).First();
        }

        // The single candidate that aired on the file's day, unless the same
        // title also sits on another date: that is duplicated data, and a
        // person should choose.
        if (candidates.Select(c => c.FoldedTitle).Distinct().Count() == candidates.Count)
        {
            var sameDay = candidates.Where(c => LocalDay(c.Event) == fileDate.Date).ToList();
            if (sameDay.Count == 1)
                return sameDay[0];
        }

        return null;
    }

    private static bool TitleWordsAgree(ShowEvent candidate, string foldedFile) =>
        candidate.TitleWords.Count > 0 && candidate.TitleWords.All(w => ContainsPhrase(foldedFile, w));

    /// <summary>The local day an event aired.</summary>
    private static DateTime LocalDay(Event evt) =>
        evt.BroadcastDate?.Date ?? evt.EventDate.Date;

    /// <summary>
    /// Days between the file's local air date and the event: against the
    /// event's local broadcast date when it has one, else against its UTC
    /// time with the file's date read as noon UTC.
    /// </summary>
    private static double DaysApart(Event evt, DateTime fileDate) =>
        evt.BroadcastDate.HasValue
            ? Math.Abs((evt.BroadcastDate.Value.Date - fileDate.Date).TotalDays)
            : Math.Abs((evt.EventDate - fileDate.Date.AddHours(12)).TotalDays);

    private static bool WithinAirDateWindow(Event evt, DateTime fileDate) =>
        evt.BroadcastDate.HasValue
            ? DaysApart(evt, fileDate) <= Math.Floor(AirDateWindowDays)
            : DaysApart(evt, fileDate) <= AirDateWindowDays;

    private static DateTime? ParseFileDate(string fileName)
    {
        foreach (Match m in FileDate.Matches(fileName))
        {
            if (DateTime.TryParseExact(
                    $"{m.Groups["y"].Value}-{m.Groups["m"].Value}-{m.Groups["d"].Value}",
                    "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }
        return null;
    }

    /// <summary>
    /// The show number the file carries: "#N", else the number straight
    /// after a show name ("Collision 33"). Never a Night/Day/Part number and
    /// never the year ("AEW.Collision.2024.02.03").
    /// </summary>
    private static int? ParseFileNumber(string fileName, string foldedFile, List<ShowEvent> pool, DateTime fileDate)
    {
        var hash = HashNumber.Match(fileName);
        if (hash.Success && int.TryParse(hash.Groups["num"].Value, out var hashNumber))
            return hashNumber;

        // Every occurrence: a TVDB-style name repeats the show ("... -
        // Collision - 2024-02-03 - AEW Collision 33"), and the first one may
        // be followed by the date.
        foreach (var phrase in pool.Select(s => s.ShowPhrase).Distinct())
        {
            foreach (Match after in Regex.Matches(foldedFile, $@"(?<![a-z0-9]){Regex.Escape(phrase)} (?<num>\d{{1,5}})(?![a-z0-9])"))
            {
                if (!int.TryParse(after.Groups["num"].Value, out var number))
                    continue;
                if (Math.Abs(number - fileDate.Year) <= 1)
                    continue;
                return number;
            }
        }
        return null;
    }

    private static List<(string Kind, int N)> Segments(string folded) =>
        SegmentMarker.Matches(folded)
            // Night, Day and Part number the same thing: which of an
            // event's sittings this is ("Night 2" file, "Day 2" event).
            .Select(m => ("segment", int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)))
            .Distinct()
            .ToList();

    private static HashSet<string> Weekdays(string folded) =>
        WeekdayNames.Where(day => ContainsPhrase(folded, day)).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> DistinctiveWords(string foldedRest, string show, string[] leagueNames)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phrase in leagueNames.Append(show))
            excluded.UnionWith(phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return foldedRest
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !w.All(char.IsDigit) && !StopWords.Contains(w) &&
                        !WeekdayNames.Contains(w) && !excluded.Contains(w))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> SplitAlternateNames(string? alternate) =>
        string.IsNullOrWhiteSpace(alternate)
            ? Enumerable.Empty<string>()
            : alternate.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Folded text holds only letters, digits and single spaces, so a whole-
    // word phrase test is a padded substring test. No per-phrase regex: a
    // scan runs this for every show event against every dated file.
    private static bool ContainsPhrase(string folded, string phrase) =>
        phrase.Length > 0 &&
        (" " + folded + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);

    private static string RemovePhrase(string folded, string phrase)
    {
        if (phrase.Length == 0)
            return folded;
        var padded = (" " + folded + " ").Replace(" " + phrase + " ", " ", StringComparison.Ordinal);
        return padded.Trim();
    }

    /// <summary>
    /// Accents stripped, lower case, apostrophes and trademark signs
    /// dropped, everything else that is not a letter or digit a single space.
    /// </summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (ch is '\'' or '’' or '®' or '™')
                continue;
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }
        return builder.ToString().Trim();
    }
}
