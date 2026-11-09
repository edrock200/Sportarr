using FluentAssertions;
using Sportarr.Api.Helpers;
using Xunit;

namespace Sportarr.Api.Tests.Helpers;

public class SeasonEpisodeTokensTests
{
    [Theory]
    [InlineData("Formula 1 - S2024E05 - Chinese Grand Prix", "Formula 1 - Chinese Grand Prix", 2024)]
    [InlineData("Formula1.S2024E05.Chinese.Grand.Prix.1080p", "Formula1.Chinese.Grand.Prix.1080p", 2024)]
    [InlineData("NFL - s2025e03 - Panthers vs Browns", "NFL - Panthers vs Browns", 2025)]
    [InlineData("Show - S01E05 - Title", "Show - Title", null)]
    [InlineData("Show.S01E05.Title.720p", "Show.Title.720p", null)]
    [InlineData("Show_S01E05_Title", "Show_Title", null)]
    [InlineData("Show - S2024E05-E06 - Double Header", "Show - Double Header", 2024)]
    [InlineData("Show - S2024E05E06 - Double Header", "Show - Double Header", 2024)]
    [InlineData("Show - S2024 E05 - Spaced", "Show - Spaced", 2024)]
    [InlineData("S01E05 - Title", "Title", null)]
    public void StripsSeasonEpisodeMarkersAndReportsYearSeasons(string input, string expected, int? expectedYear)
    {
        SeasonEpisodeTokens.Strip(input, out var year).Should().Be(expected);
        year.Should().Be(expectedYear);
    }

    [Theory]
    [InlineData("UFC 300 Pereira vs Hill")]
    [InlineData("Seattle Sounders vs Austin FC 2024-05-12")]
    [InlineData("SUPERSEASON05")] // the S/E letters are inside a word
    [InlineData("WWE.SmackDown.2024.05.10")]
    public void LeavesNamesWithoutAMarkerUnchanged(string input)
    {
        SeasonEpisodeTokens.HasMarker(input).Should().BeFalse();
        SeasonEpisodeTokens.Strip(input, out var year).Should().Be(input);
        year.Should().BeNull();
    }

    [Fact]
    public void ANameThatIsOnlyAMarkerIsKept()
    {
        SeasonEpisodeTokens.Strip("S01E05").Should().Be("S01E05");
    }
}
