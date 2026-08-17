using MqttVision.Server.Domain;
using MqttVision.Server.Infrastructure.Cad;

namespace MqttVision.Server.Tests.Infrastructure.Cad;

public sealed class CadTableExtractorTests
{
    [Fact]
    public void ParseVerticalOneDimensionalTableKeepsEmptyRowsAndLetterSuffixes()
    {
        var items = new[]
        {
            new CadTextItem("TEXT", "1D", "0", "*Model_Space", 2146, -127),
            new CadTextItem("TEXT", "1n-0213", "0", "*Model_Space", 2125, -132),
            new CadTextItem("TEXT", "1", "0", "*Model_Space", 2141, -132),
            new CadTextItem("TEXT", "A4111", "0", "*Model_Space", 2148, -132),
            new CadTextItem("TEXT", "LHa-1S1", "0", "*Model_Space", 2158, -132),
            new CadTextItem("TEXT", "13a", "0", "*Model_Space", 2141, -136),
            new CadTextItem("TEXT", "232R", "0", "*Model_Space", 2141, -138),
            new CadTextItem("TEXT", "至小母线", "0", "*Model_Space", 2141, -140),
            new CadTextItem("TEXT", "2", "0", "*Model_Space", 2141, -180),
            new CadTextItem("TEXT", "aux", "0", "*Model_Space", 2125, -180)
        };
        var source = new CadConfigurationSource { OriginalFileName = "demo.dwg" };

        var result = CadTableExtractor.Parse("demo", source, items);

        result.Configuration.TerminalStrips.Should().ContainSingle();
        result.Configuration.Terminals.Should().Contain(row => row.TerminalLabel == "1" && row.RightWireMarker == "A4111");
        result.Configuration.Terminals.Should().Contain(row => row.TerminalLabel == "13a");
        result.Configuration.ImportWarnings.Should().Contain(warning => warning.Contains("至小母线", StringComparison.Ordinal));
        result.Configuration.Terminals.Should().NotContain(row => row.TerminalLabel == "232R");
        result.Configuration.ImportWarnings.Should().Contain(warning => warning.Contains("232R", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseTwoDimensionalTableMapsAuxiliaryAndDestinationSeparately()
    {
        var items = new[]
        {
            new CadTextItem("TEXT", "2D", "0", "*Model_Space", 2220, -127),
            new CadTextItem("TEXT", "1n-0501", "0", "*Model_Space", 2201, -132),
            new CadTextItem("TEXT", "1", "0", "*Model_Space", 2216, -132),
            new CadTextItem("TEXT", "101", "0", "*Model_Space", 2223, -132),
            new CadTextItem("TEXT", "1DK-4", "0", "*Model_Space", 2234, -132),
            new CadTextItem("TEXT", "2", "0", "*Model_Space", 2216, -180)
        };
        var source = new CadConfigurationSource { OriginalFileName = "demo.dwg" };

        var result = CadTableExtractor.Parse("demo", source, items);

        result.Configuration.TerminalStrips.Should().ContainSingle(strip => strip.StripCode == "2D");
        var row = result.Configuration.Terminals.Should().ContainSingle(item => item.TerminalLabel == "1").Subject;
        row.LeftWireMarker.Should().Be("1n-0501");
        row.AuxiliaryValue.Should().Be("101");
        row.RightWireMarker.Should().Be("1DK-4");
    }
}
