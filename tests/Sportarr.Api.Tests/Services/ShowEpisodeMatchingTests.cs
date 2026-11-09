using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Sportarr.Api.Data;
using Sportarr.Api.Helpers;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;
using Xunit;

namespace Sportarr.Api.Tests.Services;

/// <summary>
/// Numbered weekly shows ("Collision #33") are matched by show and air date.
/// The schedule below is the real AEW 2024 block from the Sportarr metadata
/// hub: Dynamite on Wednesdays, Rampage on Fridays, Collision on Saturdays,
/// each stamped in UTC with its local broadcast date beside it. Before, the
/// fuzzy scorer put "All Elite Wrestling - Collision - 2024-02-03 - AEW
/// Collision 33" on the right event at 52, and even a clean scene name at 66,
/// so no weekly show ever reached the auto-import floor.
/// </summary>
public class ShowEpisodeMatchingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SportarrDbContext _db;
    private readonly LibraryImportService _service;
    private League _aew = null!;

    public ShowEpisodeMatchingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sportarr-show-match-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        var options = new DbContextOptionsBuilder<SportarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new SportarrDbContext(options);
        var fileParser = new MediaFileParser(Mock.Of<ILogger<MediaFileParser>>());
        var config = new ConfigService(new ConfigurationBuilder().Build(), Mock.Of<ILogger<ConfigService>>());
        _service = new LibraryImportService(
            _db,
            Mock.Of<ILogger<LibraryImportService>>(),
            fileParser,
            new SportsFileNameParser(Mock.Of<ILogger<SportsFileNameParser>>()),
            new FileNamingService(Mock.Of<ILogger<FileNamingService>>()),
            new EventPartDetector(Mock.Of<ILogger<EventPartDetector>>()),
            config,
            EpisodeResolverFixture.Create(_db, config),
            new DiskSpaceService(Mock.Of<ILogger<DiskSpaceService>>()),
            new CustomFormatService(fileParser),
            null!,
            Mock.Of<IMetadataWriterService>());
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static readonly (string Title, string Utc, string Broadcast, int Episode)[] Aew2024 =
    {
        ("Dynamite #225", "2024-01-25T01:00:00Z", "2024-01-24", 11),
        ("Rampage #129", "2024-01-26T00:00:00Z", "2024-01-26", 12),
        ("Collision #32", "2024-01-28T01:00:00Z", "2024-01-27", 13),
        ("Dynamite #226", "2024-02-01T01:00:00Z", "2024-01-31", 14),
        ("Rampage #130", "2024-02-02T00:00:00Z", "2024-02-02", 15),
        ("Collision #33", "2024-02-04T01:00:00Z", "2024-02-03", 16),
        ("Dynamite #227", "2024-02-08T01:00:00Z", "2024-02-07", 17),
        ("Rampage #131", "2024-02-09T00:00:00Z", "2024-02-09", 18),
        ("Collision #34", "2024-02-11T01:00:00Z", "2024-02-10", 19),
    };

    private void SeedAew(params (string Title, string Utc, string Broadcast, int Episode)[] extra)
    {
        _aew = new League { Name = "AEW", Sport = "Combat", ExternalId = "lg-000195" };
        _db.Leagues.Add(_aew);
        _db.SaveChanges();
        var i = 0;
        foreach (var row in Aew2024.Concat(extra))
        {
            var utc = DateTime.Parse(row.Utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            _db.Events.Add(new Event
            {
                Title = row.Title, Sport = "Combat", LeagueId = _aew.Id,
                Season = utc.Year.ToString(), SeasonNumber = utc.Year, Round = row.Episode.ToString(),
                EpisodeNumber = row.Episode, EventDate = utc, BroadcastDate = DateTime.Parse(row.Broadcast),
                ExternalId = $"ev-9{i++:D5}", Status = "completed", Monitored = true,
            });
        }
        _db.SaveChanges();
    }

    private Event EventTitled(string title) => _db.Events.Single(e => e.Title == title);

    private void WriteFile(string relativePath)
    {
        var path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[1024]);
    }

    private async Task<ImportableFile> ScanSingleAsync()
    {
        var result = await _service.ScanFolderAsync(_tempDir, includeSubfolders: true);
        return result.MatchedFiles.Concat(result.UnmatchedFiles).Should().ContainSingle().Subject;
    }

    [Theory]
    [InlineData("All Elite Wrestling - Collision - 2024-02-03 - AEW Collision 33 WEBDL-1080p.mkv", 95)]
    [InlineData("AEW - 2024-02-03 - AEW Collision 33 WEBDL-1080p.mkv", 95)]
    [InlineData("AEW.Collision.2024.02.03.1080p.WEB.h264-HEEL.mkv", 90)]
    [InlineData("AEW Collision #33 2024-02-03.mkv", 95)]
    public async Task ADatedCollisionFileMatchesItsAiringAboveTheAutoImportFloor(string name, int expected)
    {
        SeedAew();
        WriteFile(name);

        var file = await ScanSingleAsync();

        file.MatchedEventId.Should().Be(EventTitled("Collision #33").Id);
        file.MatchConfidence.Should().Be(expected);
        file.MatchConfidence.Should().BeGreaterThanOrEqualTo(LibraryImportService.AutoImportConfidenceFloor);
    }

    [Fact]
    public async Task TheDateDecidesWhenSportarrNumbersTheShowOneOff()
    {
        // Sportarr's running numbers can sit a show away from the real ones
        // for months; the airing on the file's date is still the one.
        SeedAew();
        WriteFile("AEW Collision 34 - 2024-02-03.mkv");

        var file = await ScanSingleAsync();

        file.MatchedEventId.Should().Be(EventTitled("Collision #33").Id);
        file.MatchConfidence.Should().Be(ShowEpisodeMatcher.AirDateConfidence);
    }

    [Fact]
    public async Task ANumberFarFromTheAiringIsLeftForAPerson()
    {
        SeedAew();
        WriteFile("AEW Collision 90 - 2024-02-03.mkv");

        var file = await ScanSingleAsync();

        (file.MatchConfidence ?? 0).Should().BeLessThan(LibraryImportService.AutoImportConfidenceFloor,
            "the file's number and Sportarr's differ by far more than drift explains");
    }

    [Fact]
    public async Task AnotherShowInTheSameWeekIsNotTaken()
    {
        // Saturday's Dynamite file: Dynamite aired Wednesday, three days off,
        // and Collision is a different show.
        SeedAew();
        WriteFile("AEW.Dynamite.2024.02.03.1080p.mkv");

        var file = await ScanSingleAsync();

        (file.MatchConfidence ?? 0).Should().BeLessThan(LibraryImportService.AutoImportConfidenceFloor,
            "the general scorer may suggest that day's Collision, but never strongly enough to import it");
    }

    [Fact]
    public async Task AnAiringThatAlreadyHasAFileIsNotTakenOrSwappedForANeighbour()
    {
        SeedAew();
        var filed = EventTitled("Collision #33");
        filed.HasFile = true;
        _db.SaveChanges();
        WriteFile("AEW.Collision.2024.02.03.1080p.mkv");

        var file = await ScanSingleAsync();

        file.MatchedEventId.Should().NotBe(filed.Id);
        (file.MatchConfidence ?? 0).Should().BeLessThan(LibraryImportService.AutoImportConfidenceFloor,
            "the neighbouring Collisions are a week away");
    }

    [Fact]
    public async Task TheFolderCanNameTheLeagueAndShow()
    {
        SeedAew();
        WriteFile(Path.Combine("AEW.Collision", "2024-02-03 - Episode.mkv"));

        var file = await ScanSingleAsync();

        file.MatchedEventId.Should().Be(EventTitled("Collision #33").Id);
        file.MatchConfidence.Should().Be(ShowEpisodeMatcher.AirDateConfidence);
    }

    [Theory]
    [InlineData("AEW Collision 85 - Slam Dunk Saturday - 2025-03-22.mkv", "Collision #85 Slam Dunk Saturday")]
    [InlineData("AEW Collision 85 - Slam Dunk Sunday - 2025-03-23.mkv", "Collision #85 Slam Dunk Sunday")]
    [InlineData("AEW Collision 85 - Slam Dunk - 2025-03-23.mkv", "Collision #85 Slam Dunk Sunday")]
    public async Task TwoNightsUnderOneNumberAreToldApartByWeekdayOrDay(string name, string expectedTitle)
    {
        SeedAew(
            ("Collision #85 Slam Dunk Saturday", "2025-03-23T00:00:00Z", "2025-03-22", 40),
            ("Collision #85 Slam Dunk Sunday", "2025-03-24T00:00:00Z", "2025-03-23", 41));
        WriteFile(name);

        var file = await ScanSingleAsync();

        file.MatchedEventId.Should().Be(EventTitled(expectedTitle).Id);
        file.MatchConfidence.Should().BeGreaterThanOrEqualTo(LibraryImportService.AutoImportConfidenceFloor);
    }

    [Fact]
    public async Task NightMarkersPickTheirNight()
    {
        SeedAew(
            ("Collision #124 Holiday Bash Day 1", "2025-12-21T01:00:00Z", "2025-12-20", 80),
            ("Collision #124 Holiday Bash Day 2", "2025-12-22T01:00:00Z", "2025-12-21", 81));
        WriteFile("AEW Collision 124 - Holiday Bash, Night 2 - 2025-12-20.mkv");

        var file = await ScanSingleAsync();

        file.MatchedEventId.Should().Be(_db.Events.Single(e => e.EpisodeNumber == 81).Id,
            "a Night 2 file is the second night even when dated a day early; the night number is not the show number");
    }

    [Fact]
    public async Task ADuplicatedUpstreamRowResolvesToTheOriginal()
    {
        SeedAew(
            ("Collision #93 Beach Break Day 2", "2025-05-18T00:00:00Z", "2025-05-17", 45),
            ("Collision #93 Beach Break Day 2", "2025-05-18T00:00:00Z", "2025-05-17", 46));
        WriteFile("AEW Collision 93 - 2025-05-17.mkv");

        var file = await ScanSingleAsync();

        file.MatchedEventId.Should().Be(_db.Events.Single(e => e.EpisodeNumber == 45).Id);
    }

    [Fact]
    public async Task TheSameNumberOnTwoDatesIsDecidedByTheFilesDay()
    {
        SeedAew(
            ("Collision #139", "2026-04-12T00:00:00Z", "2026-04-11", 60),
            ("Collision #139", "2026-04-13T00:00:00Z", "2026-04-12", 61));
        WriteFile("AEW Collision 139 - 2026-04-11.mkv");

        var file = await ScanSingleAsync();

        (file.MatchConfidence ?? 0).Should().BeLessThan(LibraryImportService.AutoImportConfidenceFloor,
            "the same title on two dates is duplicated data a person should resolve");
    }

    [Fact]
    public async Task TwoFilesForOneAiringDoNotBothClaimIt()
    {
        SeedAew();
        WriteFile("AEW.Collision.2024.02.03.1080p.mkv");
        WriteFile("AEW.Collision.2024.02.03.720p.mkv");

        var result = await _service.ScanFolderAsync(_tempDir, includeSubfolders: false);

        result.MatchedFiles.Count(f => f.MatchedEventId == EventTitled("Collision #33").Id).Should().Be(1);
    }

    [Theory]
    [InlineData("AEW", "All Elite Wrestling")]
    [InlineData("All Elite Wrestling", "AEW")]
    [InlineData("UFC", "Ultimate Fighting Championship")]
    [InlineData("WWE", "WWF")]
    public void PromotionAliasesWorkBothWays(string league, string alias)
    {
        PromotionAliases.For(league).Should().Contain(alias);
    }

    [Fact]
    public void OtherLeaguesHaveNoPromotionAliases()
    {
        PromotionAliases.For("NFL").Should().BeEmpty();
    }

    [Fact]
    public void TheFullPromotionNameIsParsedAsTheOrganization()
    {
        var parsed = new SportsFileNameParser(Mock.Of<ILogger<SportsFileNameParser>>())
            .Parse("All Elite Wrestling - Collision - 2024-02-03 - AEW Collision 33 WEBDL-1080p");

        parsed.Organization.Should().Be("AEW");
        parsed.Sport.Should().Be("Wrestling");
    }
}
