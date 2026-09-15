namespace AgentBridge.OpenAI.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Assembly_is_loadable()
    {
        Assert.NotNull(typeof(global::AgentBridge.OpenAI.AssemblyMarker).Assembly);
    }
}
