using MqttVision.Server.Application.Configuration;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Tests.Application.Configuration;

public sealed class VisualConfigurationMatcherTests
{
    private readonly VisualConfigurationMatcher matcher = new();

    [Fact]
    public void MatchUsesUniqueMarkerToLocateCabinetAndStrip()
    {
        var configuration = CreateConfiguration(
            "cabinet-a",
            "1D",
            ("1", "1n-0001", "A4111"),
            ("2", "1n-0002", "B4111"));

        var result = matcher.Match(
            CabinetConfigurationIndex.Build([configuration]),
            [new ConfigurationMarkerObservation(7, " 1N－0002 ", 0.96, 120, 80)]);

        result.Status.Should().Be("matched");
        result.CabinetId.Should().Be("cabinet-a");
        result.StripId.Should().Be("cabinet-a-1d");
        result.DistinctMatchedMarkerCount.Should().Be(1);
        result.Evidence.Should().ContainSingle(item => item.Matched && item.NormalizedText == "1N-0002");
    }

    [Fact]
    public void MatchIgnoresUnmatchedOcrAndKeepsKnownMarkerEvidence()
    {
        var configuration = CreateConfiguration("cabinet-a", "1D", ("1", "1n-0001", "A4111"));

        var result = matcher.Match(
            CabinetConfigurationIndex.Build([configuration]),
            [
                new ConfigurationMarkerObservation(1, "OCR-ERROR", 0.99, 80, 80),
                new ConfigurationMarkerObservation(2, "1n-0001", 0.88, 100, 80)
            ]);

        result.Status.Should().Be("matched");
        result.ObservedMarkerCount.Should().Be(2);
        result.MatchedMarkerCount.Should().Be(1);
        result.Evidence.Should().Contain(item => !item.Matched && item.ObservedText == "OCR-ERROR");
    }

    [Fact]
    public void MatchUsesMultipleMarkersToResolveACompetingCabinet()
    {
        var preferred = CreateConfiguration(
            "cabinet-a",
            "1D",
            ("1", "1n-0001", "A4111"),
            ("2", "1n-0002", "B4111"));
        var competing = CreateConfiguration("cabinet-b", "1D", ("1", "1n-0001", "C4111"));

        var result = matcher.Match(
            CabinetConfigurationIndex.Build([preferred, competing]),
            [
                new ConfigurationMarkerObservation(1, "1n-0001", 0.9, 90, 80),
                new ConfigurationMarkerObservation(2, "B4111", 0.9, 110, 80)
            ]);

        result.Status.Should().Be("matched");
        result.CabinetId.Should().Be("cabinet-a");
        result.DistinctMatchedMarkerCount.Should().Be(2);
    }

    [Fact]
    public void MatchReturnsAmbiguousWhenOnlyMarkerBelongsToTwoCabinets()
    {
        var first = CreateConfiguration("cabinet-a", "1D", ("1", "1n-0001", "A4111"));
        var second = CreateConfiguration("cabinet-b", "1D", ("1", "1n-0001", "B4111"));

        var result = matcher.Match(
            CabinetConfigurationIndex.Build([first, second]),
            [new ConfigurationMarkerObservation(1, "1n-0001", 0.9, 90, 80)]);

        result.Status.Should().Be("ambiguous");
        result.CabinetId.Should().BeNull();
        result.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public void MatchReturnsUnresolvedWhenNoUsableMarkerExists()
    {
        var configuration = CreateConfiguration("cabinet-a", "1D", ("1", "1n-0001", "A4111"));

        var result = matcher.Match(
            CabinetConfigurationIndex.Build([configuration]),
            [new ConfigurationMarkerObservation(1, "", null, 90, 80)]);

        result.Status.Should().Be("unresolved");
        result.Strategy.Should().Be("marker-index-no-usable-observation");
        result.ObservedMarkerCount.Should().Be(0);
        result.Rounds.Should().HaveCount(3);
    }

    [Fact]
    public void MatchUsesSeparatorInsensitiveSecondRound()
    {
        var configuration = CreateConfiguration("cabinet-a", "1D", ("1", "1n-0001", "A4111"));

        var result = matcher.Match(
            CabinetConfigurationIndex.Build([configuration]),
            [new ConfigurationMarkerObservation(1, "1n/0001", 0.9, 90, 80)]);

        result.Status.Should().Be("matched");
        result.Rounds.Should().Contain(item => item.Round == 2 && item.MatchedObservationCount == 1);
        result.Evidence.Should().ContainSingle(item => item.MatchMethod == "variant" && item.Round == 2);
    }

    [Fact]
    public void MatchCompletesThreeRoundsBeforeReportingNoConfiguration()
    {
        var configuration = CreateConfiguration("cabinet-a", "1D", ("1", "1n-0001", "A4111"));

        var result = matcher.Match(
            CabinetConfigurationIndex.Build([configuration]),
            [new ConfigurationMarkerObservation(1, "ring-cabinet-marker", 0.9, 90, 80)]);

        result.Status.Should().Be("no-configuration");
        result.Strategy.Should().Be("marker-index-three-round-no-match");
        result.Rounds.Should().HaveCount(3);
        result.Rounds[0].Name.Should().Be("exact-normalized");
        result.Rounds[1].Name.Should().Be("separator-insensitive");
        result.Rounds[2].Name.Should().Be("bounded-fuzzy");
        result.Evidence.Should().ContainSingle(item => !item.Matched && item.Round == 3);
    }

    [Fact]
    public void MatchRequiresTwoIndependentFuzzyMarkersBeforeResolving()
    {
        var configuration = CreateConfiguration(
            "cabinet-a",
            "1D",
            ("1", "1n-0001", "A4111"),
            ("2", "1n-0002", "B4111"));

        var oneMarker = matcher.Match(
            CabinetConfigurationIndex.Build([configuration]),
            [new ConfigurationMarkerObservation(1, "1n-0009", 0.9, 90, 80)]);
        oneMarker.Status.Should().Be("no-configuration");

        var twoMarkers = matcher.Match(
            CabinetConfigurationIndex.Build([configuration]),
            [
                new ConfigurationMarkerObservation(1, "1n-0009", 0.9, 90, 80),
                new ConfigurationMarkerObservation(2, "B4112", 0.9, 110, 80)
            ]);
        twoMarkers.Status.Should().Be("matched");
        twoMarkers.Candidates[0].FuzzyMatchCount.Should().Be(2);
    }

    private static CabinetConfiguration CreateConfiguration(
        string cabinetId,
        string stripCode,
        params (string Label, string Left, string Right)[] terminals)
    {
        var stripId = $"{cabinetId}-{stripCode.ToLowerInvariant()}";
        var rows = terminals
            .Select((terminal, index) => new CabinetTerminalConfiguration
            {
                TerminalNumber = index + 1,
                TerminalLabel = terminal.Label,
                LeftWireMarker = terminal.Left,
                RightWireMarker = terminal.Right,
                WireMarkers = [terminal.Left, terminal.Right],
                ExpectedWireMarker = terminal.Left,
                StripId = stripId,
                StripCode = stripCode,
                SourceOrdinal = index
            })
            .ToList();
        return new CabinetConfiguration
        {
            CabinetId = cabinetId,
            TerminalStrips =
            [
                new CabinetTerminalStripConfiguration
                {
                    StripId = stripId,
                    StripCode = stripCode,
                    Orientation = "vertical",
                    Terminals = rows
                }
            ],
            Terminals = rows
        };
    }
}
