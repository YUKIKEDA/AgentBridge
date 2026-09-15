namespace AgentBridge.Wpf.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Assembly_is_loadable()
    {
        Assert.NotNull(typeof(global::AgentBridge.Wpf.AssemblyMarker).Assembly);
    }
}
