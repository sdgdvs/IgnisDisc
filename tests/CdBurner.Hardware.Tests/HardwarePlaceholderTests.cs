using Xunit;

namespace CdBurner.Hardware.Tests;

public class HardwarePlaceholderTests
{
    [Fact]
    public void TestHardwareAssemblyLoaded()
    {
        Assert.NotNull(typeof(CdBurner.Core.Primitives.OpticalConstants));
    }
}
