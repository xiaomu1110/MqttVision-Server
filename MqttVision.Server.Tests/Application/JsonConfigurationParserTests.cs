using MqttVision.Server.Application;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Tests.Application;

public sealed class JsonConfigurationParserTests
{
    [Fact]
    public async Task ParsesRelationMapWithMultipleMarkersAndFillsNumericGaps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mqttvision-{Guid.NewGuid():N}.json");
        var json = """
            {
              "101/QF3-4": { "terminal_block": "CD", "terminal": "1" },
              "101/YK-1": { "terminal_block": "CD", "terminal": "1" },
              "101/4XS-7": { "terminal_block": "CD", "terminal": "3" },
              "115/4XS-4": { "terminal_block": "CD", "terminal": "5" },
              "872/H1-X2": { "terminal_block": "CN", "terminal": "2a" }
            }
            """;

        await File.WriteAllTextAsync(path, json);
        try
        {
            var source = new JsonConfigurationSource
            {
                OriginalFileName = "sample.json",
                Format = "json-import-v1"
            };
            var result = await new JsonConfigurationParser().ParseAsync(path, "sample", source);

            result.Format.Should().Be("wire-marker-relation-map-v1");
            result.Configuration.JsonSource.Should().Be(source);
            result.Configuration.TerminalStrips.Should().Contain(strip => strip.StripCode == "CD");

            var cd = result.Configuration.TerminalStrips.Single(strip => strip.StripCode == "CD");
            cd.Terminals.Select(terminal => terminal.TerminalLabel).Should().ContainInOrder("1", "2", "3", "4", "5");
            cd.Terminals.Single(terminal => terminal.TerminalLabel == "1").WireMarkers
                .Should().BeEquivalentTo(["101/QF3-4", "101/YK-1"]);
            cd.Terminals.Single(terminal => terminal.TerminalLabel == "2").IsExpectedEmpty.Should().BeTrue();
            cd.Terminals.Single(terminal => terminal.TerminalLabel == "4").IsExpectedEmpty.Should().BeTrue();
            cd.Terminals.Single(terminal => terminal.TerminalLabel == "5").WirePrefix.Should().Be("115");
            result.Warnings.Should().Contain(warning => warning.Contains("端子“2”", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SkipsInvalidTerminalLabelsAndKeepsValidRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mqttvision-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              "100/invalid": { "terminal_block": "CD", "terminal": "1A" },
              "100/valid": { "terminal_block": "CD", "terminal": "1a" }
            }
            """);
        try
        {
            var result = await new JsonConfigurationParser().ParseAsync(
                path,
                "sample",
                new JsonConfigurationSource { OriginalFileName = "sample.json" });

            result.Configuration.Terminals.Should().ContainSingle();
            result.Configuration.Terminals[0].TerminalLabel.Should().Be("1a");
            result.Warnings.Should().ContainSingle(warning => warning.Contains("1A", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
