namespace Atlas.Application.Tests;

public class PlaceholderTests
{
    [Fact]
    public void Application_assembly_is_reachable()
    {
        Assert.NotNull(typeof(Atlas.Application.AssemblyMarker).Assembly);
    }
}
