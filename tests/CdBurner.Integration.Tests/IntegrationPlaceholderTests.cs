using Xunit;

namespace CdBurner.Integration.Tests;

public class IntegrationPlaceholderTests
{
    [Fact]
    public void TestProjectsLoaded()
    {
        Assert.NotNull(typeof(CdBurner.Core.Primitives.OpticalConstants));
    }
}
