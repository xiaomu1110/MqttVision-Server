using MqttVision.Server.Application;

namespace MqttVision.Server.Tests.Application;

public sealed class CadImportServiceTests
{
    [Fact]
    public void CreateBatchIdUsesAnEightCharacterGuidToken()
    {
        var id = CadImportService.CreateBatchId(
            new DateTimeOffset(2026, 8, 14, 14, 41, 0, TimeSpan.FromHours(8)));

        id.Should().MatchRegex("^cad-20260814-144100-[0-9a-f]{8}$");
    }
}
