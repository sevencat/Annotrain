using Xunit;

namespace Sevencat.Train.Engine.Tests;

public class SmokeTests
{
    [Fact]
    public void SupportedModels_ContainsYoloAndRfDetr()
    {
        string[] models = Sevencat.Train.Engine.EngineModule.SupportedModels;

        Assert.Contains("yolov8", models);
        Assert.Contains("yolo11", models);
        Assert.Contains("rfdetr", models);
    }
}
