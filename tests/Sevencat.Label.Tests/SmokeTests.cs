using Xunit;

namespace Sevencat.Label.Tests;

public class SmokeTests
{
    [Fact]
    public void ProductName_IsStable()
        => Assert.Equal("Annotrain", Sevencat.Core.AnnotrainInfo.ProductName);

    [Fact]
    public void LabelModule_IsRegistered()
        => Assert.Equal("Label", Sevencat.Label.LabelModule.Name);
}
