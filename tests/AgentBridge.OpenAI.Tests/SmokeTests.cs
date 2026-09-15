namespace AgentBridge.OpenAI.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void アセンブリが存在する_マーカー型を参照する_アセンブリがロードできる()
    {
        Assert.NotNull(typeof(global::AgentBridge.OpenAI.AssemblyMarker).Assembly);
    }
}
