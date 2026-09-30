using System.Diagnostics;
using System.Globalization;
using MoonSharp.Interpreter;

namespace Enma;

/// <summary>
/// Runs one Enma (or Lua) program in a sandboxed MoonSharp interpreter. The host calls <see cref="Tick"/>
/// about every 50 ms to drive <c>on_tick</c> callbacks and timers. The top level and every callback run
/// under a watchdog, so an endless loop stops the program instead of freezing the host.
/// </summary>
public sealed class EnmaRuntime : IDisposable
{
    /// <summary>The globals this runtime adds on top of Lua's standard library.</summary>
    public static readonly IReadOnlyList<string> Globals = ["log", "clock", "on_tick", "on_unload", "timer", "time", "json", "script"];

    private const int WatchdogInstructions = 20_000;
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(250);
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    private sealed class Timer(DynValue callback, double due, double? interval)
    {
        public DynValue Callback { get; } = callback;
        public double Due { get; set; } = due;
        public double? Interval { get; } = interval;
        public bool Cancelled { get; set; }
    }

    private readonly Action<string> _output;
    private readonly List<DynValue> _tick = [];
    private readonly List<DynValue> _unload = [];
    private readonly List<Timer> _timers = [];
    private Script? _script;

    /// <param name="name">Shown in messages and used as the chunk name in error positions.</param>
    /// <param name="output">Receives everything the program logs, plus runtime errors.</param>
    public EnmaRuntime(string name, Action<string> output)
    {
        Name = name;
        _output = output;
    }

    public string Name { get; }

    /// <summary>How long the top level or one callback may run before it counts as an endless loop.</summary>
    public TimeSpan Budget { get; set; } = DefaultBudget;

    public bool IsRunning => _script != null;

    /// <summary>True while the program has tick callbacks or pending timers, i.e. it wants more <see cref="Tick"/>s.</summary>
    public bool HasWork => IsRunning && (_tick.Count > 0 || _timers.Any(t => !t.Cancelled));

    /// <summary>Compiles and starts Enma source. Compile errors and warnings go to the output; false if it didn't start.</summary>
    public bool Start(string enmaSource)
    {
        var result = EnmaCompiler.Compile(enmaSource);
        foreach (var warning in result.Warnings) _output($"[{Name}] warning {warning.Line}:{warning.Column}: {warning.Message}");
        if (result.Lua is not { } lua)
        {
            foreach (var error in result.Errors) _output($"[{Name}] error: {error.Line}:{error.Column}: {error.Message}");
            return false;
        }
        return StartLua(lua);
    }

    /// <summary>Starts a Lua 5.2 chunk with the same API.</summary>
    public bool StartLua(string lua)
    {
        Stop();
        var script = new Script(CoreModules.Preset_SoftSandbox);
        RegisterApi(script);
        _script = script;
        try
        {
            var chunk = script.LoadString(lua, codeFriendlyName: Name);
            if (!RunWatched(chunk, []))
            {
                Fail("the top level ran too long; check for an endless loop.");
                return false;
            }
            return true;
        }
        catch (InterpreterException ex)
        {
            Fail(ex.DecoratedMessage);
            return false;
        }
    }

    /// <summary>Runs tick callbacks and due timers once.</summary>
    public void Tick()
    {
        if (_script == null) return;
        foreach (var callback in _tick.ToArray())
            if (!SafeCall(callback)) return;

        double now = Uptime.Elapsed.TotalSeconds;
        foreach (var timer in _timers.ToArray())
        {
            if (!timer.Cancelled && timer.Due > now) continue;
            if (timer.Cancelled || timer.Interval is not { } interval)
            {
                _timers.Remove(timer);
                if (timer.Cancelled) continue;
                timer.Cancelled = true;
            }
            else
            {
                // After a stall, keep the rhythm from now on instead of catching up.
                timer.Due += interval;
                if (timer.Due < now) timer.Due = now + interval;
            }
            if (!SafeCall(timer.Callback)) return;
        }
    }

    /// <summary>Runs the <c>on_unload</c> callbacks and drops the program.</summary>
    public void Stop()
    {
        if (_script == null) return;
        foreach (var callback in _unload.ToArray()) SafeCall(callback);
        Clear();
    }

    public void Dispose() => Stop();

    private void Clear()
    {
        _script = null;
        _tick.Clear();
        _unload.Clear();
        _timers.Clear();
    }

    private void Fail(string message)
    {
        _output($"[{Name}] error: {message}");
        Clear();
    }

    private bool RunWatched(DynValue fn, DynValue[] args)
    {
        var coroutine = _script!.CreateCoroutine(fn).Coroutine;
        coroutine.AutoYieldCounter = WatchdogInstructions;
        var stopwatch = Stopwatch.StartNew();
        coroutine.Resume(args);
        // ForceSuspended = hit the auto-yield while still working.
        while (coroutine.State == CoroutineState.ForceSuspended)
        {
            if (stopwatch.Elapsed > Budget) return false;
            coroutine.Resume();
        }
        return true;
    }

    private bool SafeCall(DynValue callback, params DynValue[] args)
    {
        if (_script == null) return false;
        try
        {
            if (RunWatched(callback, args)) return true;
            Fail("a callback ran too long; check for an endless loop.");
        }
        catch (InterpreterException ex)
        {
            Fail(ex.DecoratedMessage);
        }
        return false;
    }

    private static string Text(DynValue value) => value.Type == DataType.String ? value.String : value.ToPrintString();

    private void RegisterApi(Script script)
    {
        var g = script.Globals;
        var log = DynValue.NewCallback((_, a) =>
        {
            _output($"[{Name}] " + string.Join("\t", Enumerable.Range(0, a.Count).Select(i => Text(a[i]))));
            return DynValue.Nil;
        });
        g["log"] = log;
        g["print"] = log;
        g["clock"] = (Func<string>)(() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        g["on_tick"] = (Action<DynValue>)(fn => { if (fn.Type == DataType.Function) _tick.Add(fn); });
        g["on_unload"] = (Action<DynValue>)(fn => { if (fn.Type == DataType.Function) _unload.Add(fn); });
        g["script"] = new Table(script) { ["name"] = Name };

        var timer = new Table(script);
        timer["after"] = DynValue.NewCallback((_, a) => Schedule(script, a, repeat: false));
        timer["every"] = DynValue.NewCallback((_, a) => Schedule(script, a, repeat: true));
        g["timer"] = timer;

        var time = new Table(script);
        time["ms"] = (Func<double>)(() => Math.Floor(Uptime.Elapsed.TotalMilliseconds));
        time["now"] = (Func<double>)(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        time["format"] = DynValue.NewCallback((_, a) =>
        {
            string pattern = a.Count > 0 && a[0].Type == DataType.String ? a[0].String : "HH:mm:ss";
            try
            {
                return DynValue.NewString(DateTime.Now.ToString(pattern, CultureInfo.InvariantCulture));
            }
            catch (FormatException)
            {
                throw new ScriptRuntimeException($"time.format: bad pattern '{pattern}'");
            }
        });
        g["time"] = time;

        // MoonSharp's json module is serialize/parse; expose it as encode/decode.
        if (script.DoString("return json") is { Type: DataType.Table } json)
        {
            json.Table["encode"] = json.Table.Get("serialize");
            json.Table["decode"] = json.Table.Get("parse");
        }

        if (g.Get("string") is { Type: DataType.Table } str)
        {
            var s = str.Table;
            s["split"] = DynValue.NewCallback((c, a) =>
            {
                string text = Arg(a, 0, "split");
                string sep = a.Count > 1 && a[1].Type == DataType.String ? a[1].String : " ";
                var list = new Table(c.GetScript());
                foreach (string part in sep.Length == 0 ? [text] : text.Split(sep)) list.Append(DynValue.NewString(part));
                return DynValue.NewTable(list);
            });
            s["trim"] = DynValue.NewCallback((_, a) => DynValue.NewString(Arg(a, 0, "trim").Trim()));
            s["starts_with"] = DynValue.NewCallback((_, a) => DynValue.NewBoolean(Arg(a, 0, "starts_with").StartsWith(Arg(a, 1, "starts_with"), StringComparison.Ordinal)));
            s["ends_with"] = DynValue.NewCallback((_, a) => DynValue.NewBoolean(Arg(a, 0, "ends_with").EndsWith(Arg(a, 1, "ends_with"), StringComparison.Ordinal)));
            s["contains"] = DynValue.NewCallback((_, a) => DynValue.NewBoolean(Arg(a, 0, "contains").Contains(Arg(a, 1, "contains"), StringComparison.Ordinal)));
        }

        if (g.Get("math") is { Type: DataType.Table } math)
        {
            var m = math.Table;
            m["clamp"] = (Func<double, double, double, double>)((x, min, max) => Math.Min(Math.Max(x, min), max));
            m["lerp"] = (Func<double, double, double, double>)((from, to, t) => from + (to - from) * t);
            m["round"] = DynValue.NewCallback((_, a) =>
            {
                double x = a.Count > 0 && a[0].Type == DataType.Number ? a[0].Number : 0;
                int decimals = a.Count > 1 && a[1].Type == DataType.Number ? (int)a[1].Number : 0;
                return DynValue.NewNumber(Math.Round(x, Math.Clamp(decimals, 0, 15), MidpointRounding.AwayFromZero));
            });
        }
    }

    private static string Arg(CallbackArguments a, int index, string function) =>
        a.Count > index && a[index].Type is DataType.String or DataType.Number
            ? a[index].CastToString()
            : throw new ScriptRuntimeException($"string.{function}: argument {index + 1} must be a string");

    private DynValue Schedule(Script script, CallbackArguments a, bool repeat)
    {
        string name = repeat ? "timer.every" : "timer.after";
        if (a.Count < 2 || a[0].Type != DataType.Number || a[1].Type != DataType.Function)
            throw new ScriptRuntimeException($"{name}(seconds, fn) expects a number and a function");
        double seconds = repeat ? Math.Max(0.05, a[0].Number) : Math.Max(0, a[0].Number);
        var timer = new Timer(a[1], Uptime.Elapsed.TotalSeconds + seconds, repeat ? seconds : null);
        _timers.Add(timer);

        var handle = new Table(script);
        handle["cancel"] = DynValue.NewCallback((_, _) =>
        {
            timer.Cancelled = true;
            return DynValue.Nil;
        });
        handle["active"] = DynValue.NewCallback((_, _) => DynValue.NewBoolean(!timer.Cancelled));
        return DynValue.NewTable(handle);
    }
}
