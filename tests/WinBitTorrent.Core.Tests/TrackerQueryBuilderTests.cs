using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class TrackerQueryBuilderTests
{
    private const string RussianSeasonFormat = "сезон {0}";

    [Fact]
    public void EnglishTitleKeepsTheSeasonWordInEnglish()
        => Assert.Equal(
            "Bleach season 1",
            TrackerQueryBuilder.Build(new CatalogTrackerQuery("Bleach", 2004, 1, IsEnglishTitle: true), RussianSeasonFormat));

    // Without an English title the query stays in the UI language, so the season word follows it
    // rather than producing a mixed "Блич season 1".
    [Fact]
    public void LocalizedTitleUsesTheLocalizedSeasonWord()
        => Assert.Equal(
            "Блич сезон 1",
            TrackerQueryBuilder.Build(new CatalogTrackerQuery("Блич", 2004, 1, IsEnglishTitle: false), RussianSeasonFormat));

    [Fact]
    public void MovieQueryAppendsTheYearInsteadOfASeason()
        => Assert.Equal(
            "Inception 2010",
            TrackerQueryBuilder.Build(new CatalogTrackerQuery("Inception", 2010, Season: null, IsEnglishTitle: true), RussianSeasonFormat));

    [Fact]
    public void MovieQueryWithoutAYearIsJustTheTitle()
        => Assert.Equal(
            "Inception",
            TrackerQueryBuilder.Build(new CatalogTrackerQuery("Inception", Year: null, Season: null, IsEnglishTitle: true), RussianSeasonFormat));

    // A series is searched by name and season; the release year of season 1 only narrows it wrongly.
    [Fact]
    public void SeasonQueryDropsTheYear()
        => Assert.Equal(
            "Bleach season 2",
            TrackerQueryBuilder.Build(new CatalogTrackerQuery("Bleach", 2004, 2, IsEnglishTitle: true), RussianSeasonFormat));
}
