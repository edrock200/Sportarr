using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

/// <summary>
/// Resolves the episode number used in media paths and metadata.
/// </summary>
public class EpisodeNumberResolver
{
    private readonly SportarrDbContext _db;
    private readonly SportarrApiClient _api;
    private readonly ILogger<EpisodeNumberResolver> _logger;
    private readonly Dictionary<string, Task<int?>> _exactNumbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<Dictionary<string, int>?>> _seasonSnapshots = new();

    public EpisodeNumberResolver(
        SportarrDbContext db,
        SportarrApiClient api,
        ILogger<EpisodeNumberResolver> logger)
    {
        _db = db;
        _api = api;
        _logger = logger;
    }

    public async Task<int> ResolveAsync(
        Event eventInfo,
        IReadOnlyDictionary<string, int>? seasonSnapshot = null)
    {
        int? authoritativeNumber = null;

        if (!string.IsNullOrWhiteSpace(eventInfo.ExternalId)
            && seasonSnapshot != null
            && seasonSnapshot.TryGetValue(eventInfo.ExternalId, out var snapshotNumber)
            && snapshotNumber > 0)
        {
            authoritativeNumber = snapshotNumber;
        }
        else if (!string.IsNullOrWhiteSpace(eventInfo.ExternalId))
        {
            if (!_exactNumbers.TryGetValue(eventInfo.ExternalId, out var exactTask))
            {
                exactTask = _api.GetEpisodeNumberFromApiAsync(eventInfo.ExternalId);
                _exactNumbers[eventInfo.ExternalId] = exactTask;
            }
            authoritativeNumber = await exactTask;
        }

        return await ApplyAuthoritativeOrFallbackAsync(eventInfo, authoritativeNumber);
    }

    public async Task<int> ResolveBatchAsync(Event eventInfo)
    {
        var snapshot = await GetSeasonSnapshotAsync(eventInfo);
        if (snapshot.Attempted && snapshot.Numbers == null)
            return await ApplyAuthoritativeOrFallbackAsync(eventInfo, null);

        return await ResolveAsync(eventInfo, snapshot.Numbers);
    }

    /// <summary>
    /// The season snapshot's number when the hub returned one for this
    /// event, otherwise exactly <see cref="ResolveAsync"/>'s per-event
    /// lookup. Unlike <see cref="ResolveBatchAsync"/>, a missing or failed
    /// snapshot never skips the per-event request, so the result always
    /// agrees with what an import resolves. Lets a scan that previews many
    /// events of one season make one request instead of one per event.
    /// </summary>
    public async Task<int> ResolvePreferSeasonAsync(Event eventInfo)
    {
        var snapshot = await GetSeasonSnapshotAsync(eventInfo);
        return await ResolveAsync(eventInfo, snapshot.Numbers);
    }

    private async Task<int> ApplyAuthoritativeOrFallbackAsync(Event eventInfo, int? authoritativeNumber)
    {
        if (authoritativeNumber is > 0)
        {
            if (eventInfo.EpisodeNumber != authoritativeNumber)
            {
                _logger.LogInformation(
                    "[Episode Number] Corrected {EventId} from E{OldNumber} to E{EpisodeNumber}",
                    eventInfo.ExternalId, eventInfo.EpisodeNumber, authoritativeNumber);
                eventInfo.EpisodeNumber = authoritativeNumber;
            }

            return authoritativeNumber.Value;
        }

        if (eventInfo.EpisodeNumber is > 0)
        {
            _logger.LogWarning(
                "[Episode Number] The hub did not return {EventId}. Using stored episode E{EpisodeNumber}",
                eventInfo.ExternalId, eventInfo.EpisodeNumber);
            return eventInfo.EpisodeNumber.Value;
        }

        var localNumber = await CalculateLocalNumberAsync(eventInfo);
        eventInfo.EpisodeNumber = localNumber;
        _logger.LogWarning(
            "[Episode Number] The hub did not return {EventId}. Using local episode E{EpisodeNumber}",
            eventInfo.ExternalId, localNumber);
        return localNumber;
    }

    private async Task<SeasonSnapshot> GetSeasonSnapshotAsync(Event eventInfo)
    {
        if (!eventInfo.LeagueId.HasValue)
            return new SeasonSnapshot(false, null);

        var league = eventInfo.League ?? await _db.Leagues.FindAsync(eventInfo.LeagueId.Value);
        if (string.IsNullOrWhiteSpace(league?.ExternalId))
            return new SeasonSnapshot(false, null);

        var season = GetSeason(eventInfo);
        var key = $"{league.ExternalId}\n{season}";
        if (!_seasonSnapshots.TryGetValue(key, out var snapshotTask))
        {
            snapshotTask = _api.GetEpisodeNumbersFromApiAsync(league.ExternalId, season);
            _seasonSnapshots[key] = snapshotTask;
        }

        return new SeasonSnapshot(true, await snapshotTask);
    }

    private async Task<int> CalculateLocalNumberAsync(Event eventInfo)
    {
        if (!eventInfo.LeagueId.HasValue)
            return 1;

        var query = _db.Events.Where(e => e.LeagueId == eventInfo.LeagueId);
        if (!string.IsNullOrWhiteSpace(eventInfo.Season))
        {
            var season = eventInfo.Season;
            var seasonNumber = eventInfo.SeasonNumber;
            query = query.Where(e => e.Season == season
                || (seasonNumber.HasValue && e.Season == null && e.SeasonNumber == seasonNumber));
        }
        else if (eventInfo.SeasonNumber.HasValue)
        {
            var seasonNumber = eventInfo.SeasonNumber.Value;
            var season = seasonNumber.ToString();
            query = query.Where(e => e.SeasonNumber == seasonNumber
                || (!e.SeasonNumber.HasValue && e.Season == season));
        }
        else
        {
            var seasonYear = (eventInfo.BroadcastDate ?? eventInfo.EventDate).Year;
            query = query.Where(e => (e.BroadcastDate ?? e.EventDate).Year == seasonYear);
        }

        var eventKeys = await query
            .Where(e => e.Status != "Postponed" && e.Status != "postponed"
                        && e.Status != "Cancelled" && e.Status != "cancelled"
                        && e.Status != "Canceled" && e.Status != "canceled")
            .OrderBy(e => e.EventDate)
            .ThenBy(e => e.ExternalId)
            .ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.EventDate, e.ExternalId })
            .ToListAsync();

        var position = eventKeys.FindIndex(e => e.Id == eventInfo.Id);
        if (position >= 0)
            return position + 1;

        position = eventKeys.Count(e => e.EventDate < eventInfo.EventDate
            || (e.EventDate == eventInfo.EventDate
                && string.Compare(e.ExternalId ?? string.Empty, eventInfo.ExternalId ?? string.Empty,
                    StringComparison.Ordinal) < 0));
        return position + 1;
    }

    private static string GetSeason(Event eventInfo)
    {
        return eventInfo.Season
            ?? eventInfo.SeasonNumber?.ToString()
            ?? (eventInfo.BroadcastDate ?? eventInfo.EventDate).Year.ToString();
    }

    private readonly record struct SeasonSnapshot(
        bool Attempted,
        IReadOnlyDictionary<string, int>? Numbers);
}
