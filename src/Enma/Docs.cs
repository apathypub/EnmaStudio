namespace Enma;

/// <summary>The language and runtime reference, shown in Enma Studio and given to the agents.</summary>
public static class EnmaDocs
{
    public const string Reference =
        """
        Enma (.enma) is a small scripting language that compiles to Lua 5.2. Generated Lua keeps the Enma
        line numbers, so runtime errors point at the right line.

        Comments         // line    /* block */
        Variables        let x = 1     let a, b = f()     const LIMIT = 10  (can't be reassigned)
        Assignment       x = 2   x += 1   -= *= /= %=   s ++= "text"    a, b = b, a
        Strings          "hello {name}, you have {count + 1} points"   (interpolation; \{ for a brace)
                         'raw {not interpolated}'     concat: "a" ++ "b"
        Numbers          1  2.5  0xFF  1_000_000      ** is power, % modulo
        Logic            && || !   (or: and or not)   == != < > <= >=
        Nil handling     value ?? fallback            (fallback only when value is nil)
        Ternary          cond ? a : b
        Pipe             x |> f(y)  ==  f(x, y)       x |> f  ==  f(x)
        Lists            [1, 2, 3]   (1-based like Lua; #list is the length; list[#list + 1] = v appends)
        Maps             {name: "x", "key with space": 1, [expr]: v, shorthand}   m.name  m["key"]
        Functions        fn add(a, b = 1) { return a + b }     fn sq(x) => x * x
                         let f = fn(x) => x * 2      fn(a, b) { ... }      varargs: fn f(...) { }
                         Functions in a block are hoisted: they may call each other in any order
                         (but top-level code only runs a function after its fn statement has run).
        Methods          obj:method(args)  calls with self;   fn obj:method() { self.v }   fn obj.helper() { }
        If               if a { } elif b { } else { }     (also "else if")
        Loops            while cond { }
                         for i in 0..10 { }      (0..9, end exclusive)   for i in 1..=10 { }  (inclusive)
                         for i in 10..0 step -2 { }
                         for v in list { }       for i, v in list { }    for k, v in map { }
                         break, continue
        Classes          class Dog : Animal {
                             fn init(name) { self.name = name }
                             fn speak() { return "{self.name} barks" }
                         }
                         let d = Dog("rex")   d:speak()    Dog.super is the base class
        Event sugar      on tick { }                 == on_tick(fn() { })        (every 50 ms)
                         on unload { }               == on_unload(fn() { })
                         on source "event" (e) { }   == source.on("event", fn(e) { })
                         every 1.5 { }  after 3 { }  == timer.every / timer.after
        Return           return a, b   ("return" alone on its line ends the function)
        Statements need no semicolons (optional ;). Blocks always use braces.

        Compiler checks: syntax errors, writes to const, break/continue outside loops (errors);
        unknown names and implicit globals (warnings).
        """;

    public const string Runtime =
        """
        Runtime (MoonSharp, Lua 5.2 compatible, sandboxed: no io, no os.execute, no loading files):
        - log(...), print(...)           write to the Output panel
        - clock()                        current time as "HH:mm:ss"
        - on_tick(fn)                    fn runs every 50 ms while the program runs
        - on_unload(fn)                  fn runs when the program is stopped
        - timer.after(seconds, fn), timer.every(seconds, fn) -> t: t:cancel(), t:active()
        - time.ms() (ms since start), time.now() (unix seconds), time.format(".NET pattern")
        - json.encode(table) -> text, json.decode(text) -> table
        - string additions (also as methods): s:split(sep), s:trim(), s:starts_with(x), s:ends_with(x), s:contains(x)
        - math additions: math.clamp(x, min, max), math.lerp(a, b, t), math.round(x, decimals)
        - script.name                    the program's name
        Callbacks must return quickly: the top level and each callback have a 250 ms budget.
        """;

    /// <summary>A short, idiomatic program, used in the docs and in agent prompts.</summary>
    public const string Example =
        """
        // Counts ticks and reports every second; stops itself after five reports.
        class Counter {
            fn init(label) { self.label = label; self.n = 0 }
            fn bump() { self.n += 1 }
            fn report() => "{self.label}: {self.n}"
        }

        let ticks = Counter("ticks")
        let reports = 0

        on tick { ticks:bump() }

        let t = nil
        t = timer.every(1, fn() {
            reports += 1
            log(ticks:report())
            if reports >= 5 { t:cancel() }
        })
        """;
}
