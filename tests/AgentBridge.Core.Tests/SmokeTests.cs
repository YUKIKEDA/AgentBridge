namespace AgentBridge.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void アセンブリが存在する_マーカー型を参照する_アセンブリがロードできる()
    {
        Assert.NotNull(typeof(global::AgentBridge.Core.AssemblyMarker).Assembly);
    }
}
