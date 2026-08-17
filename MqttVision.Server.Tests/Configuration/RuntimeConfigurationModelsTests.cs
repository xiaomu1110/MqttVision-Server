using MqttVision.Server.Configuration;

namespace MqttVision.Server.Tests.Configuration;

public sealed class RuntimeConfigurationModelsTests
{
    [Fact]
    public void Admin_form_to_options_treats_null_string_values_as_empty()
    {
        var form = new AdminConfigurationForm
        {
            PublicBaseUrl = null!,
            StorageRoot = null!,
            Processing = new AdminProcessingConfigurationForm
            {
                YoloOnnxModelPath = null!,
                PaddleOcrModelDirectory = null!,
                PaddleOcrDeploymentMode = null!,
                PaddleOcrServiceUrl = null!,
                PaddleOcrCommand = null!,
                PaddleOcrArgumentsTemplate = null!,
                PaddleOcrWorkingDirectory = null!,
                PaddleOcrAdditionalPath = null!,
                CabinetConfigurationRoot = null!
            }
        };

        var options = form.ToOptions();

        options.PublicBaseUrl.Should().BeEmpty();
        options.StorageRoot.Should().BeEmpty();
        options.Processing.YoloOnnxModelPath.Should().BeEmpty();
        options.Processing.PaddleOcrModelDirectory.Should().BeEmpty();
        options.Processing.PaddleOcrDeploymentMode.Should().BeEmpty();
        options.Processing.PaddleOcrServiceUrl.Should().BeEmpty();
        options.Processing.PaddleOcrCommand.Should().BeEmpty();
        options.Processing.PaddleOcrArgumentsTemplate.Should().BeEmpty();
        options.Processing.PaddleOcrWorkingDirectory.Should().BeEmpty();
        options.Processing.PaddleOcrAdditionalPath.Should().BeEmpty();
        options.Processing.CabinetConfigurationRoot.Should().BeEmpty();
    }
}
