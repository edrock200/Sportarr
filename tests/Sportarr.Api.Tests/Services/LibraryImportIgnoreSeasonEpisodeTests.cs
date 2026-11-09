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
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;
using Xunit;

namespace Sportarr.Api.Tests.Services;

/// <summary>
/// Media named by another tool against TheTVDB carries TVDB's episode
/// numbers. The scanner treats an S/E marker as authoritative, so a TVDB
/// "E03" vetoes the right event whenever Sportarr numbers it differently.
/// The "Ignore S#E#" scan option strips the marker and matches on the rest
/// of the name, keeping a year-shaped season as the year.
/// </summary>
public class LibraryImportIgnoreSeasonEpisodeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SportarrDbContext _db;
    private readonly LibraryImportService _service;

    public LibraryImportIgnoreSeasonEpisodeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sportarr-ignore-se-tests-" + Guid.NewGuid());
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

    private (Event panthers, Event ravens) Seed()
    {
        var nfl = new League { Name = "NFL", Sport = "American Football", ExternalId = "lg-000032" };
        _db.Leagues.Add(nfl);
        _db.SaveChanges();
        var panthers = new Event
        {
            Title = "Carolina Panthers vs Cleveland Browns", Sport = "American Football", Season = "2025",
            EventDate = new DateTime(2025, 8, 8), LeagueId = nfl.Id, ExternalId = "ev-312923",
            SeasonNumber = 2025, EpisodeNumber = 6, Status = "completed", HasFile = false, Monitored = true,
        };
        // Sportarr's episode 3 is a different game: the TVDB number would
        // send the file here, or nowhere.
        var ravens = new Event
        {
            Title = "Baltimore Ravens vs Detroit Lions", Sport = "American Football", Season = "2025",
            EventDate = new DateTime(2025, 9, 22), LeagueId = nfl.Id, ExternalId = "ev-313015",
            SeasonNumber = 2025, EpisodeNumber = 3, Status = "completed", HasFile = false, Monitored = true,
        };
        _db.Events.AddRange(panthers, ravens);
        _db.SaveChanges();
        return (panthers, ravens);
    }

    private void WriteFile(string relativePath)
    {
        var path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64 * 1024]);
    }

    private const string TvdbName = "NFL - S2025E03 - Carolina Panthers vs Cleveland Browns.mkv";

    [Fact]
    public async Task WithoutTheOptionAForeignEpisodeNumberVetoesTheRightEvent()
    {
        var (panthers, _) = Seed();
        WriteFile(TvdbName);

        var result = await _service.ScanFolderAsync(_tempDir, includeSubfolders: false);

        result.MatchedFiles.Should().NotContain(f => f.MatchedEventId == panthers.Id,
            "the S/E marker is authoritative by default and names Sportarr's episode 3");
    }

    [Fact]
    public async Task WithTheOptionTheFileMatchesOnItsTitle()
    {
        var (panthers, ravens) = Seed();
        WriteFile(TvdbName);

        var result = await _service.ScanFolderAsync(_tempDir, includeSubfolders: false,
            ignoreSeasonEpisode: true);

        var match = result.MatchedFiles.Should().ContainSingle().Subject;
        match.MatchedEventId.Should().Be(panthers.Id);
        match.MatchedEventId.Should().NotBe(ravens.Id);
        match.OriginalTitle.Should().Be(Path.GetFileNameWithoutExtension(TvdbName),
            "the stripped name is only for matching; the file keeps its own name");
    }

    [Fact]
    public async Task WithTheOptionTheSeasonYearStillGuardsAgainstOtherSeasons()
    {
        var (panthers, _) = Seed();
        WriteFile("NFL - S2019E03 - Carolina Panthers vs Cleveland Browns.mkv");

        var result = await _service.ScanFolderAsync(_tempDir, includeSubfolders: false,
            ignoreSeasonEpisode: true);

        result.MatchedFiles.Should().NotContain(f => f.MatchedEventId == panthers.Id,
            "the 2019 season is a different meeting of the same teams");
    }

    [Fact]
    public async Task ScanDoesNotDescendIntoExcludedFolders()
    {
        Seed();
        WriteFile(Path.Combine("Season 2025", TvdbName));
        WriteFile(Path.Combine("@eaDir", "thumb.mkv"));
        WriteFile(Path.Combine(".Trash", "Season 2025", "old.mkv"));
        WriteFile(Path.Combine("Season 2025", "notes.txt"));

        var result = await _service.ScanFolderAsync(_tempDir, includeSubfolders: true,
            ignoreSeasonEpisode: true);

        result.TotalFiles.Should().Be(1);
        result.Errors.Should().BeEmpty();
        result.MatchedFiles.Concat(result.UnmatchedFiles).Single().FileName.Should().Be(TvdbName);
    }
}
