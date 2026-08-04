using MqttVision.Server.Domain;

namespace MqttVision.Server.Tests.Domain;

public class DetectionBoxTests
{
    [Theory]
    [InlineData(0f, 0f, 10f, 10f, 10f, 10f)]
    [InlineData(5f, 5f, 20f, 30f, 25f, 35f)]
    [InlineData(-3f, -7f, 1f, 1f, -2f, -6f)]
    public void Right_and_Bottom_are_x_plus_width_and_y_plus_height(
        float x, float y, float width, float height, float expectedRight, float expectedBottom)
    {
        var box = new DetectionBox(x, y, width, height);

        box.Right.Should().Be(expectedRight);
        box.Bottom.Should().Be(expectedBottom);
    }

    [Theory]
    [InlineData(0f, 0f, 10f, 20f, 5f, 10f)]
    [InlineData(100f, 200f, 50f, 60f, 125f, 230f)]
    public void CenterX_and_CenterY_are_offset_by_half_extent(
        float x, float y, float width, float height, float expectedCenterX, float expectedCenterY)
    {
        var box = new DetectionBox(x, y, width, height);

        box.CenterX.Should().Be(expectedCenterX);
        box.CenterY.Should().Be(expectedCenterY);
    }

    [Fact]
    public void Record_value_equality_ignores_reference_identity()
    {
        var first = new DetectionBox(1, 2, 3, 4);
        var second = new DetectionBox(1, 2, 3, 4);

        first.Should().Be(second);
        (first == second).Should().BeTrue();
    }
}
