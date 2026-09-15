namespace AgentBridge.OpenAI.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void AssemblyMarker_型を参照するとアセンブリがロードできること()
    {
        Assert.NotNull(typeof(global::AgentBridge.OpenAI.AssemblyMarker).Assembly);
    }
}
