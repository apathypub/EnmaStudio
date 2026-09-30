namespace Enma.Tests;

public class RuntimeTests
{
    [Fact]
    public void TickCallbacksAndTimersRunOnTick()
    {
        var output = new List<string>();
        using var runtime = new EnmaRuntime("t", output.Add);
        Assert.True(runtime.Start("""
            let ticks = 0
            on tick { ticks += 1 }
            after 0 { log("after {ticks}") }
            """));
        Assert.True(runtime.HasWork);
        runtime.Tick();
        runtime.Tick();
        Assert.Contains("[t] after 1", output);
    }

    [Fact]
    public void CancelledTimersStop()
    {
        var output = new List<string>();
        using var runtime = new EnmaRuntime("t", output.Add);
        Assert.True(runtime.Start("let t = timer.every(0.05, fn() { log(\"x\") })\nt:cancel()"));
        runtime.Tick();
        Assert.DoesNotContain("[t] x", output);
    }

    [Fact]
    public void EndlessLoopsAreStopped()
    {
        var output = new List<string>();
        using var runtime = new EnmaRuntime("loop", output.Add) { Budget = TimeSpan.FromMilliseconds(50) };
        Assert.False(runtime.Start("while true { }"));
        Assert.False(runtime.IsRunning);
        Assert.Contains(output, line => line.Contains("endless loop"));
    }

    [Fact]
    public void UnloadCallbacksRunOnStop()
    {
        var output = new List<string>();
        var runtime = new EnmaRuntime("u", output.Add);
        Assert.True(runtime.Start("on unload { log(\"bye\") }"));
        runtime.Stop();
        Assert.Contains("[u] bye", output);
    }

    [Fact]
    public void StandardAdditions()
    {
        var output = new List<string>();
        using var runtime = new EnmaRuntime("s", output.Add);
        Assert.True(runtime.Start("""
            log(#("a,b,c"):split(","))
            log(("  hi  "):trim())
            log(math.clamp(15, 0, 10))
            log(json.decode(json.encode({n: 5})).n)
            """));
        Assert.Equal(["[s] 3", "[s] hi", "[s] 10", "[s] 5"], output);
    }
}
