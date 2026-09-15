namespace AgentBridge.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void AssemblyMarker_型を参照するとアセンブリがロードできること()
    {
        Assert.NotNull(typeof(global::AgentBridge.Core.AssemblyMarker).Assembly);
    }
}
