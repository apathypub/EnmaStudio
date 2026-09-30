# Enma Studio

**Enma** is a small, brace-style scripting language that compiles to Lua 5.2.
**Enma Studio** is a Windows editor for it, with live diagnostics, a compiled-Lua preview and a
team of agents (planner, coder, reviewer) that can write and review scripts for you.

```enma
class Counter {
    fn init(label) { self.label = label; self.n = 0 }
    fn bump() { self.n += 1 }
    fn report() => "{self.label}: {self.n}"
}

let ticks = Counter("ticks")
on tick { ticks:bump() }

every 1 { log(ticks:report()) }
```

## What's inside

| Path | What it is |
| --- | --- |
| `src/Enma` | The compiler (lexer, parser, Lua code generator) and a sandboxed runtime on [MoonSharp](https://www.moonsharp.org/). No UI dependencies. |
| `src/Enma.Cli` | `enmac`: check, compile and run `.enma` files from the command line. |
| `src/EnmaStudio` | The WPF editor and the Studio panel (agent team, training, evals). |
| `tests/Enma.Tests` | xUnit tests for the compiler and runtime. |
| `samples` | Example programs; copied into a new workspace on first start. |

## The language

- `let` / `const`, functions with default parameters and `=> expr` bodies, lambdas
- string interpolation: `"hi {name}, {count + 1} points"`
- lists `[1, 2, 3]` and maps `{name: "x", [key]: v}`
- `if / elif / else`, `while`, `for i in 0..10 step 2`, `for k, v in map`, `break`, `continue`
- classes with inheritance: `class Dog : Animal { fn init(name) { ... } }`
- `??`, `cond ? a : b`, pipes `x |> f(y)`
- event sugar: `on tick { }`, `every 1.5 { }`, `after 3 { }`

The generated Lua keeps the line numbers of the Enma source, so runtime errors point at the right line.
The compiler reports syntax errors and writes to `const` values as errors, and unknown names or implicit
globals as warnings. The full reference is in the Studio's **Docs** tab and in
[`src/Enma/Docs.cs`](src/Enma/Docs.cs).

### Runtime API

`log`, `print`, `clock`, `on_tick`, `on_unload`, `timer.after` / `timer.every`, `time.ms` / `time.now` /
`time.format`, `json.encode` / `json.decode`, plus `split`, `trim`, `starts_with`, `ends_with`, `contains`
on strings and `math.clamp`, `math.lerp`, `math.round`. Programs run sandboxed (no file or OS access);
the top level and every callback get a 250 ms budget, so an endless loop stops the program instead of
freezing the host.

## Enma Studio

- explorer for a workspace folder, tabs, find/replace, completion (Ctrl+Space)
- errors and warnings underlined while you type (F8 jumps to the next one)
- **F5** runs the open file with the built-in runtime, **Shift+F5** stops it
- **Ctrl+E** opens the Studio panel:
  - **Team**: the planner writes a plan, the coder edits files and runs `check_script` on them, the
    reviewer approves or sends it back (0 to 3 review rounds). Rate answers with thumbs up/down.
  - **Train**: each agent's model, effort and instructions, plus the lessons (from thumbs down) and
    examples (from thumbs up) that are added to its prompt. "Distill lessons" folds lessons into the
    instructions; "Export dataset" writes the examples as JSONL.
  - **Evals**: test tasks with checks (`exists`, `compiles`, `contains`, `not_contains`, `approved`),
    each run in its own sandbox folder, with a score history per agent version.
  - **Lua**: the compiled Lua of the open file (Ctrl+Shift+B), with export.
  - **Docs**: the language reference.

The agents use the [Anthropic API](https://console.anthropic.com) and need an API key: enter it in the
Studio panel (it is stored DPAPI-encrypted for your Windows user) or set `ANTHROPIC_API_KEY`.
API usage is billed to your Anthropic account.

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The Studio is Windows-only (WPF);
the compiler, runtime and CLI run anywhere .NET does.

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project src/EnmaStudio
```

```bash
dotnet run --project src/Enma.Cli -- run samples/timers.enma
```

A ready-made build is attached to the [Releases](../../releases) page (it needs the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)).

## Where things are stored

| What | Where |
| --- | --- |
| Scripts | `Documents\Enma` (change it with the folder button in the explorer) |
| API key, agent profiles, evals, history | `%APPDATA%\EnmaStudio` |

Set `ENMASTUDIO_HOME` to a folder to keep all of it there instead (portable mode).

## Embedding the compiler

```csharp
using Enma;

var result = EnmaCompiler.Compile(source, globals: ["window", "player"]);
foreach (var d in result.Diagnostics) Console.WriteLine(d);
if (result.Lua is { } lua) { /* run it in any Lua 5.2 host */ }
```
