using System.Globalization;
using System.Text;

namespace Enma;

/// <summary>
/// Compiles Enma to Lua 5.2. Every statement is written on the same line number it has in the Enma
/// file, so runtime errors ("chunk:12: attempt to call nil") point at the right line.
/// Besides syntax errors it reports unknown names (warnings) and writes to consts (errors).
/// </summary>
public static class EnmaCompiler
{
    public const string Version = "0.1";

    /// <summary>
    /// Compiles <paramref name="source"/>. <paramref name="globals"/> lists the names the host provides
    /// (defaults to <see cref="KnownGlobals"/>); other undeclared names produce warnings.
    /// </summary>
    public static EnmaResult Compile(string source, IEnumerable<string>? globals = null)
    {
        List<Stmt> program;
        try
        {
            program = EnmaParser.ParseProgram(source);
        }
        catch (EnmaSyntaxException ex)
        {
            return new EnmaResult(null, [ex.Diagnostic]);
        }

        var generator = new Generator(globals == null ? KnownGlobals : [.. LuaGlobals, .. globals]);
        string lua = generator.Program(program);
        var diagnostics = generator.Diagnostics.OrderBy(d => d.Line).ThenBy(d => d.Column).ToList();
        return new EnmaResult(diagnostics.Any(d => d.Severity == EnmaSeverity.Error) ? null : lua, diagnostics);
    }

    /// <summary>Lua's standard library, always available.</summary>
    public static readonly HashSet<string> LuaGlobals =
    [
        "print", "pairs", "ipairs", "next", "type", "tostring", "tonumber", "select", "error", "assert", "pcall",
        "xpcall", "setmetatable", "getmetatable", "rawget", "rawset", "rawequal", "rawlen", "unpack", "require", "load",
        "string", "table", "math", "os", "coroutine", "bit32", "_G", "_VERSION",
    ];

    /// <summary>Lua's standard library plus the API of <see cref="EnmaRuntime"/>.</summary>
    public static readonly HashSet<string> KnownGlobals = [.. LuaGlobals, .. EnmaRuntime.Globals];

    // Lua keywords that are ordinary names in Enma; such names get a "_" suffix.
    private static readonly HashSet<string> LuaOnlyKeywords =
        ["end", "then", "do", "function", "local", "repeat", "until", "goto", "elseif"];

    private static readonly HashSet<string> LuaKeywords =
    [
        .. LuaOnlyKeywords, "and", "break", "else", "false", "for", "if", "in", "nil", "not", "or", "return", "true", "while",
    ];

    private sealed class Symbol(bool isConst)
    {
        public bool IsConst { get; } = isConst;
    }

    private sealed record Loop(string? Flag);

    private sealed class Generator(HashSet<string> globals)
    {
        private readonly HashSet<string> _globals = globals;
        private readonly StringBuilder _out = new();
        private int _line = 1;
        private readonly List<Dictionary<string, Symbol>> _scopes = [];
        // Loops of the function being generated; a function body starts a fresh stack.
        private Stack<Loop> _loops = new();
        private int _flagCounter;
        private bool _usesIter;

        public List<EnmaDiagnostic> Diagnostics { get; } = [];

        public string Program(List<Stmt> program)
        {
            Block(program);
            // The helper goes in front of line 1 without a newline so line numbers stay aligned.
            string prelude = _usesIter ? IterHelper + " " : "";
            return prelude + _out;
        }

        private const string IterHelper =
            "local function __enma_iter(t, two) if type(t) == \"function\" then return t end " +
            "if type(t) ~= \"table\" then error(\"can't loop over a \" .. type(t), 2) end " +
            "if #t > 0 or next(t) == nil then local i = 0 return function() i = i + 1 local v = t[i] " +
            "if v ~= nil then if two then return i, v end return v end end end " +
            "local k return function() local v k, v = next(t, k) if k ~= nil then if two then return k, v end return v end end end";

        // ---------------- Output ----------------

        private void W(string text) => _out.Append(text);

        /// <summary>Moves the output down to <paramref name="line"/> (never up).</summary>
        private void Sync(int line)
        {
            if (_line >= line)
            {
                if (_out.Length > 0 && _out[^1] != ' ' && _out[^1] != '\n') _out.Append(' ');
                return;
            }
            while (_line < line)
            {
                _out.Append('\n');
                _line++;
            }
        }

        private void Warn(Node node, string message, int length = 1) =>
            Diagnostics.Add(new EnmaDiagnostic(node.Line, node.Column, length, message, EnmaSeverity.Warning));

        private void Fail(Node node, string message, int length = 1) =>
            Diagnostics.Add(new EnmaDiagnostic(node.Line, node.Column, length, message, EnmaSeverity.Error));

        // ---------------- Scopes ----------------

        private void Declare(string name, bool isConst = false) => _scopes[^1][name] = new Symbol(isConst);

        private Symbol? Resolve(string name)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(name, out var symbol)) return symbol;
            return null;
        }

        private static string Mangle(string name) => LuaOnlyKeywords.Contains(name) ? name + "_" : name;

        /// <summary>True when <paramref name="name"/> can be written as a bare Lua name (obj.name, name = v).</summary>
        private static bool IsIdentifier(string name) =>
            name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_')
            && !LuaKeywords.Contains(name);

        private void CheckRead(NameExpr name)
        {
            if (Resolve(name.Name) == null && !_globals.Contains(name.Name))
                Warn(name, $"unknown name '{name.Name}' (not declared with let/fn and not part of the API)", name.Name.Length);
        }

        // ---------------- Blocks ----------------

        private void Block(List<Stmt> body)
        {
            _scopes.Add([]);
            Hoist(body);
            foreach (var stmt in body) Statement(stmt);
            _scopes.RemoveAt(_scopes.Count - 1);
        }

        /// <summary>Functions and classes of a block are declared up front so they can call each other in any order.</summary>
        private void Hoist(List<Stmt> body)
        {
            var names = new List<string>();
            foreach (var stmt in body)
            {
                string? name = stmt switch
                {
                    FnStmt { Path.Count: 1 } fn => fn.Path[0],
                    ClassStmt cls => cls.Name,
                    _ => null,
                };
                if (name == null) continue;
                if (names.Contains(name)) Fail(stmt, $"'{name}' is defined twice in this block", name.Length);
                else names.Add(name);
                Declare(name);
            }
            if (names.Count > 0)
            {
                Sync(body[0].Line);
                W("local " + string.Join(", ", names.Select(Mangle)) + " ");
            }
        }

        // ---------------- Statements ----------------

        private void Statement(Stmt stmt)
        {
            Sync(stmt.Line);
            switch (stmt)
            {
                case LetStmt let:
                    Let(let);
                    break;
                case AssignStmt assign:
                    Assign(assign);
                    break;
                case ExprStmt e:
                    Expr(e.Expr);
                    break;
                case IfStmt ifs:
                    for (int i = 0; i < ifs.Branches.Count; i++)
                    {
                        W(i == 0 ? "if " : " elseif ");
                        Expr(ifs.Branches[i].Condition);
                        W(" then ");
                        Block(ifs.Branches[i].Body);
                    }
                    if (ifs.Else != null)
                    {
                        W(" else ");
                        Block(ifs.Else);
                    }
                    W(" end");
                    break;
                case WhileStmt w:
                    W("while ");
                    Expr(w.Condition);
                    W(" do ");
                    LoopBody(w.Body, null);
                    W(" end");
                    break;
                case ForRangeStmt f:
                    ForRange(f);
                    break;
                case ForInStmt f:
                    _usesIter = true;
                    W($"for {Mangle(f.First)}{(f.Second != null ? ", " + Mangle(f.Second) : "")} in __enma_iter(");
                    Expr(f.Source);
                    W(f.Second != null ? ", true) do " : ") do ");
                    LoopBody(f.Body, f.Second != null ? [f.First, f.Second] : [f.First]);
                    W(" end");
                    break;
                case ReturnStmt r:
                    W("do return");
                    if (r.Values.Count > 0)
                    {
                        W(" ");
                        ExprList(r.Values);
                    }
                    W(" end");
                    break;
                case BreakStmt b:
                    if (_loops.Count == 0) Fail(b, "'break' outside a loop", 5);
                    else if (_loops.Peek().Flag is { } flag) W($"{flag} = true break");
                    else W("do break end");
                    break;
                case ContinueStmt c:
                    if (_loops.Count == 0) Fail(c, "'continue' outside a loop", 8);
                    else W("do break end"); // leaves the repeat...until true wrapper, see LoopBody
                    break;
                case FnStmt fn:
                    FnDeclaration(fn);
                    break;
                case ClassStmt cls:
                    Class(cls);
                    break;
                case OnStmt on:
                    On(on);
                    break;
                case TimerStmt t:
                    W(t.Repeat ? "timer.every(" : "timer.after(");
                    Expr(t.Seconds);
                    W(", function(");
                    FunctionBody([], t.Body, isMethod: false);
                    W(" end)");
                    break;
                default:
                    Fail(stmt, "unsupported statement");
                    break;
            }
        }

        private void Let(LetStmt let)
        {
            if (let.Values.Count > let.Names.Count)
                Warn(let, $"{let.Values.Count} values for {let.Names.Count} name(s); the extra values are dropped", 3);
            W("local " + string.Join(", ", let.Names.Select(Mangle)));
            if (let.Values.Count > 0)
            {
                W(" = ");
                ExprList(let.Values); // evaluated before the names exist, like Lua
            }
            foreach (string name in let.Names) Declare(name, let.IsConst);
        }

        private void Assign(AssignStmt assign)
        {
            foreach (var target in assign.Targets)
            {
                if (target is not NameExpr name) continue;
                var symbol = Resolve(name.Name);
                if (symbol is { IsConst: true }) Fail(name, $"'{name.Name}' is a const and can't be changed", name.Name.Length);
                else if (symbol == null && !_globals.Contains(name.Name))
                    Warn(name, $"'{name.Name}' isn't declared; this creates a global. Use 'let {name.Name} = ...' for a local.", name.Name.Length);
            }

            for (int i = 0; i < assign.Targets.Count; i++)
            {
                if (i > 0) W(", ");
                Target(assign.Targets[i]);
            }
            W(" = ");
            if (assign.Op == "=")
            {
                ExprList(assign.Values);
                return;
            }

            if (assign.Values.Count != 1) Fail(assign, $"'{assign.Op}' takes one value");
            string op = assign.Op[..^1] switch { "++" => "..", var o => o };
            W("(");
            Target(assign.Targets[0]);
            W($" {op} (");
            Expr(assign.Values[0]);
            W("))");
        }

        private void Target(Expr target)
        {
            if (target is NameExpr name) W(Mangle(name.Name));
            else Expr(target);
        }

        /// <summary>
        /// A loop body. With <c>continue</c> inside, the body runs in <c>repeat ... until true</c> so
        /// continue is a break out of that; a real break sets a flag checked right after it.
        /// </summary>
        private void LoopBody(List<Stmt> body, List<string>? loopVars)
        {
            bool usesContinue = ContainsContinue(body);
            string? flag = usesContinue ? $"__brk{++_flagCounter}" : null;
            _loops.Push(new Loop(flag));
            _scopes.Add([]);
            if (loopVars != null) foreach (string v in loopVars) Declare(v);
            if (flag != null) W($"local {flag} = false repeat ");
            Block(body);
            if (flag != null) W($" until true if {flag} then break end");
            _scopes.RemoveAt(_scopes.Count - 1);
            _loops.Pop();
        }

        private static bool ContainsContinue(List<Stmt> body) => body.Any(stmt => stmt switch
        {
            ContinueStmt => true,
            IfStmt ifs => ifs.Branches.Any(b => ContainsContinue(b.Body)) || (ifs.Else != null && ContainsContinue(ifs.Else)),
            _ => false, // nested loops and functions have their own continue
        });

        private void ForRange(ForRangeStmt f)
        {
            W($"for {Mangle(f.Var)} = ");
            Expr(f.From);
            W(", ");
            if (f.Inclusive)
            {
                Expr(f.To);
            }
            else
            {
                // Exclusive end: stop half a step early, which works for whole, fractional and negative steps.
                W("(");
                Expr(f.To);
                if (f.Step == null) W(") - 0.5");
                else
                {
                    W(") - (");
                    Expr(f.Step);
                    W(") / 2");
                }
            }
            if (f.Step != null)
            {
                if (f.Step is NumberExpr { Text: "0" } zero) Fail(zero, "step can't be 0");
                W(", ");
                Expr(f.Step);
            }
            W(" do ");
            LoopBody(f.Body, [f.Var]);
            W(" end");
        }

        private void FnDeclaration(FnStmt fn)
        {
            if (fn.Path.Count > 1 && Resolve(fn.Path[0]) == null && !_globals.Contains(fn.Path[0]))
                Warn(fn, $"unknown name '{fn.Path[0]}'", fn.Path[0].Length);

            var path = new StringBuilder(Mangle(fn.Path[0]));
            for (int i = 1; i < fn.Path.Count; i++)
            {
                if (!IsIdentifier(fn.Path[i])) Fail(fn, $"'{fn.Path[i]}' can't be used as a function name");
                path.Append(i == fn.Path.Count - 1 && fn.IsMethod ? ':' : '.').Append(fn.Path[i]);
            }
            W($"function {path}(");
            FunctionBody(fn.Params, fn.Body, fn.IsMethod);
            W(" end");
        }

        /// <summary>Writes "params) body" — the caller has written "function name(" and writes " end".</summary>
        private void FunctionBody(List<Param> parameters, List<Stmt> body, bool isMethod)
        {
            W(string.Join(", ", parameters.Select(p => p.Name == "..." ? "..." : Mangle(p.Name))) + ") ");
            var savedLoops = _loops;
            _loops = new Stack<Loop>();
            _scopes.Add([]);
            if (isMethod) Declare("self");
            foreach (var p in parameters)
            {
                if (p.Name == "...") continue;
                Declare(p.Name);
                if (p.Default == null) continue;
                W($"if {Mangle(p.Name)} == nil then {Mangle(p.Name)} = ");
                Expr(p.Default);
                W(" end ");
            }
            Block(body);
            _scopes.RemoveAt(_scopes.Count - 1);
            _loops = savedLoops;
        }

        private void Class(ClassStmt cls)
        {
            string name = Mangle(cls.Name);
            if (cls.Base != null && Resolve(cls.Base) == null && !_globals.Contains(cls.Base))
                Warn(cls, $"unknown base class '{cls.Base}'", cls.Name.Length);

            // Calling the class makes an instance and runs init(self, ...).
            W($"{name} = setmetatable({{}}, {{");
            if (cls.Base != null) W($"__index = {Mangle(cls.Base)}, ");
            W("__call = function(c, ...) local o = setmetatable({}, c) if c.init then c.init(o, ...) end return o end}) ");
            W($"{name}.__index = {name}");
            if (cls.Base != null) W($" {name}.super = {Mangle(cls.Base)}");
            foreach (var method in cls.Methods)
            {
                Sync(method.Line);
                if (!IsIdentifier(method.Path[1])) Fail(method, $"'{method.Path[1]}' can't be used as a method name");
                W($"function {name}:{method.Path[1]}(");
                FunctionBody(method.Params, method.Body, isMethod: true);
                W(" end");
            }
        }

        private void On(OnStmt on)
        {
            if (on.Source is "tick" or "unload" && on.Args.Count == 0)
            {
                W($"on_{on.Source}(function(");
            }
            else
            {
                if (Resolve(on.Source) == null && !_globals.Contains(on.Source))
                    Warn(on, $"unknown event source '{on.Source}' (use tick, unload, or an object with an on() function)", 2);
                W($"{Mangle(on.Source)}.on(");
                foreach (var arg in on.Args)
                {
                    Expr(arg);
                    W(", ");
                }
                W("function(");
            }
            FunctionBody(on.Params, on.Body, isMethod: false);
            W(" end)");
        }

        // ---------------- Expressions ----------------

        private void ExprList(List<Expr> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) W(", ");
                Expr(list[i]);
            }
        }

        /// <summary>An expression Lua accepts before . [ ( : — anything else gets parentheses.</summary>
        private void Prefix(Expr expr)
        {
            bool bare = expr is NameExpr or MemberExpr or IndexExpr or CallExpr or MethodCallExpr or ParenExpr;
            if (!bare) W("(");
            Expr(expr);
            if (!bare) W(")");
        }

        private static string Quote(string text)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in text)
            {
                sb.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\0' => "\\0",
                    < ' ' => "\\" + ((int)c).ToString(CultureInfo.InvariantCulture),
                    _ => c.ToString(),
                });
            }
            return sb.Append('"').ToString();
        }

        private void Expr(Expr expr)
        {
            switch (expr)
            {
                case NumberExpr n:
                    W(n.Text);
                    break;
                case StringExpr s:
                    if (s.Parts.Count == 1 && s.Parts[0] is string only)
                    {
                        W(Quote(only));
                        break;
                    }
                    W("(");
                    for (int i = 0; i < s.Parts.Count; i++)
                    {
                        if (i > 0) W(" .. ");
                        if (s.Parts[i] is string text) W(Quote(text));
                        else
                        {
                            W("tostring(");
                            Expr((Expr)s.Parts[i]);
                            W(")");
                        }
                    }
                    W(")");
                    break;
                case LiteralExpr l:
                    W(l.Lua);
                    break;
                case NameExpr name:
                    CheckRead(name);
                    W(Mangle(name.Name));
                    break;
                case MemberExpr m:
                    Prefix(m.Target);
                    W(IsIdentifier(m.Name) ? "." + m.Name : $"[{Quote(m.Name)}]");
                    break;
                case IndexExpr ix:
                    Prefix(ix.Target);
                    W("[");
                    Expr(ix.Index);
                    W("]");
                    break;
                case CallExpr call:
                    Prefix(call.Function);
                    W("(");
                    ExprList(call.Args);
                    W(")");
                    break;
                case MethodCallExpr mc:
                    Prefix(mc.Target);
                    if (!IsIdentifier(mc.Name)) Fail(mc, $"'{mc.Name}' can't be called with ':'; use obj[\"{mc.Name}\"](obj, ...)");
                    W($":{mc.Name}(");
                    ExprList(mc.Args);
                    W(")");
                    break;
                case ParenExpr p:
                    W("(");
                    Expr(p.Inner);
                    W(")");
                    break;
                case BinaryExpr b:
                    W("(");
                    Expr(b.Left);
                    W(b.Op switch
                    {
                        "!=" => " ~= ",
                        "&&" => " and ",
                        "||" => " or ",
                        "++" => " .. ",
                        "**" => " ^ ",
                        var op => $" {op} ",
                    });
                    Expr(b.Right);
                    W(")");
                    break;
                case UnaryExpr u:
                    // "- -x" must not become the Lua comment "--x".
                    W(u.Op switch { "!" => "(not ", "-" => "(- ", _ => "(#" });
                    Expr(u.Operand);
                    W(")");
                    break;
                case TernaryExpr t:
                    if (t.Then is NumberExpr or StringExpr or ListExpr or MapExpr or FnExpr or LiteralExpr { Lua: "true" })
                    {
                        // The "then" value is never false/nil, so and/or is exact.
                        W("(");
                        Expr(t.Condition);
                        W(" and ");
                        Expr(t.Then);
                        W(" or ");
                        Expr(t.Else);
                        W(")");
                    }
                    else
                    {
                        W("(function() if ");
                        Expr(t.Condition);
                        W(" then return ");
                        Expr(t.Then);
                        W(" else return ");
                        Expr(t.Else);
                        W(" end end)()");
                    }
                    break;
                case CoalesceExpr c:
                    W("(function() local __v = ");
                    Expr(c.Left);
                    W(" if __v == nil then return ");
                    Expr(c.Right);
                    W(" end return __v end)()");
                    break;
                case ListExpr list:
                    W("{");
                    ExprList(list.Items);
                    W("}");
                    break;
                case MapExpr map:
                    W("{");
                    for (int i = 0; i < map.Entries.Count; i++)
                    {
                        if (i > 0) W(", ");
                        var entry = map.Entries[i];
                        if (entry.Name != null)
                            W(IsIdentifier(entry.Name) ? entry.Name + " = " : $"[{Quote(entry.Name)}] = ");
                        else
                        {
                            W("[");
                            Expr(entry.Key!);
                            W("] = ");
                        }
                        Expr(entry.Value);
                    }
                    W("}");
                    break;
                case FnExpr fn:
                    W("function(");
                    FunctionBody(fn.Params, fn.Body!, isMethod: false);
                    W(" end");
                    break;
                default:
                    Fail(expr, "unsupported expression");
                    break;
            }
        }
    }
}
