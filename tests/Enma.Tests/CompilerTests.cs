namespace Enma.Tests;

public class CompilerTests
{
    /// <summary>Compiles and runs <paramref name="source"/>; returns everything it logged, joined with "|".</summary>
    private static string Run(string source)
    {
        var output = new List<string>();
        using var runtime = new EnmaRuntime("test", line => output.Add(line.StartsWith("[test] ") ? line[7..] : line));
        bool started = runtime.Start(source);
        Assert.True(started, string.Join("\n", output));
        return string.Join("|", output);
    }

    private static string Diagnostics(string source) =>
        string.Join("\n", EnmaCompiler.Compile(source).Diagnostics);

    [Fact]
    public void VariablesStringsAndCompoundAssignment() => Assert.Equal("x=7 name=enma|8|abc", Run("""
        let x = 1 + 2 * 3
        const name = "enma"
        log("x={x} name={name}")
        x += 1
        log(x)
        let s = "a" ++ "b"
        s ++= "c"
        log(s)
        """));

    [Fact]
    public void FunctionsAreHoistedAndTakeDefaults() => Assert.Equal("6|hi world|hi bob", Run("""
        fn twice(n) { return helper(n) * 2 }
        fn helper(n) => n
        log(twice(3))
        fn greet(who = "world") { return "hi {who}" }
        log(greet())
        log(greet("bob"))
        """));

    [Fact]
    public void Loops() => Assert.Equal("0,1,3,4|22|10|5|0|0.25|0.5|0.75|4|12", Run("""
        let out = []
        for i in 0..5 { if i == 2 { continue } out[#out + 1] = i }
        log(table.concat(out, ","))
        let t = 0
        for i in 1..=10 step 3 { t += i }
        log(t)
        for i in 10..0 step -5 { log(i) }
        for i in 0..1 step 0.25 { log(i) }
        let n = 0
        while true { n += 1 if n > 3 { break } }
        log(n)
        let found = 0
        for v in [5, 6, 7, 8] { if v == 6 { continue } if v == 8 { break } found += v }
        log(found)
        """));

    [Fact]
    public void MapsAndIteration() => Assert.Equal("3|1=x|2=y|7|two|5", Run("""
        let m = {a: 1, b: 2}
        let sum = 0
        for k, v in m { sum += v }
        log(sum)
        for i, v in ["x", "y"] { log("{i}={v}") }
        let pos = {x: 3, "y": 4, [1 + 1]: "two"}
        log(pos.x + pos.y)
        log(pos[2])
        let end = 5
        log(end)
        """));

    [Fact]
    public void ClassesWithInheritance() => Assert.Equal("rex barks|cat makes a sound", Run("""
        class Animal {
          fn init(name) { self.name = name }
          fn speak() { return "{self.name} makes a sound" }
        }
        class Dog : Animal {
          fn speak() { return "{self.name} barks" }
        }
        log(Dog("rex"):speak())
        log(Animal("cat"):speak())
        """));

    [Fact]
    public void Operators() => Assert.Equal("default|false|yes|false|15|8|1024|false|good|3|3|2|7|ABC", Run("""
        let a = nil
        log(a ?? "default")
        let f = false
        log(f ?? "x")
        log(3 > 2 ? "yes" : "no")
        let flag = false
        log(true ? flag : "other")
        fn add(x, y) => x + y
        log(5 |> add(10))
        let double = fn(v) => v * 2
        log(4 |> double)
        log(2 ** 10)
        log(!true || false)
        log(10 % 3 != 1 and "bad" or "good")
        log(- -3)
        log(#[1, 2, 3])
        let o = {count: 0}
        fn o.bump(n) { o.count += n }
        o.bump(2)
        log(o.count)
        let obj = {v: 7}
        fn obj:get() { return self.v }
        log(obj:get())
        log(("abc"):upper())
        """));

    [Fact]
    public void Varargs() => Assert.Equal("3|3|1", Run("""
        fn count(...) { return select("#", ...) }
        log(count(1, 2, 3))
        fn two() { return 1, 2 }
        let p, q = two()
        log(p + q)
        log((two()))
        """));

    [Fact]
    public void EventSugar() => Assert.Equal("clicked 3", Run("""
        let button = {on: fn(e, f) { f({count: 3}) }}
        on button "click" (e) { log("clicked {e.count}") }
        """));

    [Fact]
    public void GeneratedLuaKeepsLineNumbers()
    {
        string source = "let a = 1\n\n\nlog(a)\nfn f() {\n  return 1\n}\nlog(f())";
        var result = EnmaCompiler.Compile(source);
        Assert.True(result.Success);
        Assert.True(result.Lua!.Split('\n').Length <= source.Split('\n').Length);
    }

    [Fact]
    public void RuntimeErrorsPointAtTheEnmaLine()
    {
        var output = new List<string>();
        using var runtime = new EnmaRuntime("lines", output.Add);
        Assert.False(runtime.Start("let a = 1\n\n\nlet b = nil\nlog(b.c)"));
        Assert.Contains(output, line => line.Contains("(5,"));
    }

    [Theory]
    [InlineData("let x = ", "expected a value")]
    [InlineData("const a = 1\na = 2", "is a const")]
    [InlineData("log(foo)", "unknown name 'foo'")]
    [InlineData("fn f() {\n log(1)\n", "missing '}'")]
    [InlineData("break", "outside a loop")]
    [InlineData("let x = 1\nx == 2", "use '=' to assign")]
    [InlineData("let s = \"abc", "unclosed string")]
    public void ReportsProblems(string source, string expected) => Assert.Contains(expected, Diagnostics(source));

    [Fact]
    public void HostCanDeclareItsOwnGlobals()
    {
        Assert.Contains("unknown name 'window'", Diagnostics("window.show()"));
        Assert.Empty(EnmaCompiler.Compile("window.show()", ["window"]).Diagnostics);
    }
}
