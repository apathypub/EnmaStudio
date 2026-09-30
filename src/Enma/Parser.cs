namespace Enma;

/// <summary>
/// Recursive-descent parser for Enma. Statements need no separators (an optional <c>;</c> is allowed);
/// blocks use braces. Stops at the first syntax error.
/// </summary>
internal sealed class EnmaParser
{
    private readonly List<Token> _tokens;
    private int _pos;
    // Inside the "then" branch of a ternary, "a : b(x)" is the ternary's colon, not a method call.
    private int _ternaryDepth;

    public EnmaParser(List<Token> tokens) => _tokens = tokens;

    public static List<Stmt> ParseProgram(string source)
    {
        var parser = new EnmaParser(new EnmaLexer(source).Tokenize());
        var body = new List<Stmt>();
        while (parser.Current.Kind != TokenKind.End) body.Add(parser.Statement());
        return body;
    }

    // ---------------- Helpers ----------------

    private Token Current => _tokens[_pos];
    private Token PeekAt(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];

    private Token Advance() => _tokens[_pos < _tokens.Count - 1 ? _pos++ : _pos];

    private bool MatchSymbol(string symbol)
    {
        if (!Current.IsSymbol(symbol)) return false;
        _pos++;
        return true;
    }

    private bool MatchKeyword(string keyword)
    {
        if (!Current.IsKeyword(keyword)) return false;
        _pos++;
        return true;
    }

    private static string Describe(Token token) => token.Kind switch
    {
        TokenKind.End => "end of file",
        TokenKind.String => "a string",
        TokenKind.Number => $"number {token.Text}",
        _ => $"'{token.Text}'",
    };

    private EnmaSyntaxException Error(string message, Token? at = null)
    {
        var token = at ?? Current;
        return new EnmaSyntaxException(new EnmaDiagnostic(token.Line, token.Column, Math.Max(1, token.Text.Length), message, EnmaSeverity.Error));
    }

    private Token ExpectSymbol(string symbol, string? context = null)
    {
        if (Current.IsSymbol(symbol)) return Advance();
        throw Error($"expected '{symbol}'{(context != null ? " " + context : "")} but found {Describe(Current)}");
    }

    private string ExpectName(string what)
    {
        if (Current.Kind == TokenKind.Name) return Advance().Text;
        if (Current.Kind == TokenKind.Keyword) throw Error($"'{Current.Text}' is a keyword and can't be used as {what}");
        throw Error($"expected {what} but found {Describe(Current)}");
    }

    // ---------------- Statements ----------------

    private List<Stmt> Block(string context)
    {
        var open = ExpectSymbol("{", context);
        int saved = _ternaryDepth;
        _ternaryDepth = 0;
        var body = new List<Stmt>();
        while (!Current.IsSymbol("}"))
        {
            if (Current.Kind == TokenKind.End) throw Error($"missing '}}': the block opened on line {open.Line} is never closed", open);
            body.Add(Statement());
        }
        Advance();
        _ternaryDepth = saved;
        return body;
    }

    private Stmt Statement()
    {
        var stmt = StatementCore();
        MatchSymbol(";");
        return stmt;
    }

    private Stmt StatementCore()
    {
        var start = Current;
        if (start.Kind == TokenKind.Keyword)
        {
            switch (start.Text)
            {
                case "let":
                case "const":
                    return Let();
                case "fn" when PeekAt(1).Kind == TokenKind.Name:
                    return FnDeclaration();
                case "class":
                    return Class();
                case "if":
                    Advance();
                    return If(start);
                case "while":
                {
                    Advance();
                    var condition = Expression();
                    return new WhileStmt(condition, Block("after while"), start.Line, start.Column);
                }
                case "for":
                    return For();
                case "return":
                {
                    Advance();
                    var values = new List<Expr>();
                    // Values must start on the same line: "return" alone ends the function.
                    if (Current.Line == start.Line && !Current.IsSymbol("}") && !Current.IsSymbol(";") && Current.Kind != TokenKind.End)
                        values = ExpressionList();
                    return new ReturnStmt(values, start.Line, start.Column);
                }
                case "break":
                    Advance();
                    return new BreakStmt(start.Line, start.Column);
                case "continue":
                    Advance();
                    return new ContinueStmt(start.Line, start.Column);
                case "on":
                    return On();
                case "every":
                case "after":
                {
                    Advance();
                    var seconds = Expression();
                    return new TimerStmt(start.Text == "every", seconds, Block($"after {start.Text} <seconds>"), start.Line, start.Column);
                }
                case "elif":
                case "else":
                    throw Error($"'{start.Text}' without a matching 'if'");
            }
        }

        // Assignment or call.
        var targets = new List<Expr> { Expression() };
        while (MatchSymbol(",")) targets.Add(Expression());

        if (Current.Kind == TokenKind.Symbol && Current.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "++=")
        {
            var op = Advance();
            foreach (var target in targets)
                if (target is not (NameExpr or MemberExpr or IndexExpr))
                    throw new EnmaSyntaxException(new EnmaDiagnostic(target.Line, target.Column, 1, "can't assign to this expression", EnmaSeverity.Error));
            if (op.Text != "=" && targets.Count != 1) throw Error($"'{op.Text}' works on one target at a time", op);
            return new AssignStmt(targets, op.Text, ExpressionList(), start.Line, start.Column);
        }

        if (targets.Count > 1) throw Error("expected '=' after the list of targets");
        if (targets[0] is not (CallExpr or MethodCallExpr))
        {
            if (targets[0] is BinaryExpr { Op: "==" } compare)
                throw new EnmaSyntaxException(new EnmaDiagnostic(compare.Line, compare.Column, 2,
                    "'==' compares values; use '=' to assign", EnmaSeverity.Error));
            throw new EnmaSyntaxException(new EnmaDiagnostic(start.Line, start.Column, Math.Max(1, start.Text.Length),
                "this expression does nothing on its own (expected a call or an assignment)", EnmaSeverity.Error));
        }
        return new ExprStmt(targets[0], start.Line, start.Column);
    }

    private Stmt Let()
    {
        var keyword = Advance();
        bool isConst = keyword.Text == "const";
        var names = new List<string> { ExpectName("a variable name") };
        while (MatchSymbol(",")) names.Add(ExpectName("a variable name"));

        var values = new List<Expr>();
        if (MatchSymbol("=")) values = ExpressionList();
        else if (isConst) throw Error("a const needs a value: const NAME = ...");
        return new LetStmt(names, values, isConst, keyword.Line, keyword.Column);
    }

    private FnStmt FnDeclaration()
    {
        var keyword = Advance();
        var path = new List<string> { ExpectName("a function name") };
        bool isMethod = false;
        while (true)
        {
            if (MatchSymbol(".")) path.Add(ExpectName("a function name"));
            else if (Current.IsSymbol(":") && !isMethod)
            {
                Advance();
                path.Add(ExpectName("a method name"));
                isMethod = true;
            }
            else break;
        }
        var (parameters, body) = FunctionRest(keyword);
        return new FnStmt(path, isMethod, parameters, body, keyword.Line, keyword.Column);
    }

    /// <summary>Parameters and body; "=> expr" becomes a single return.</summary>
    private (List<Param>, List<Stmt>) FunctionRest(Token keyword)
    {
        var parameters = Parameters();
        if (Current.IsSymbol("=>"))
        {
            var arrow = Advance();
            var value = Expression();
            return (parameters, [new ReturnStmt([value], arrow.Line, arrow.Column)]);
        }
        return (parameters, Block("to start the function body"));
    }

    private List<Param> Parameters()
    {
        ExpectSymbol("(", "to start the parameters");
        var parameters = new List<Param>();
        if (!Current.IsSymbol(")"))
        {
            do
            {
                if (MatchSymbol("..."))
                {
                    parameters.Add(new Param("...", null));
                    break;
                }
                string name = ExpectName("a parameter name");
                if (parameters.Any(p => p.Name == name)) throw Error($"duplicate parameter '{name}'", _tokens[_pos - 1]);
                Expr? defaultValue = MatchSymbol("=") ? Expression() : null;
                parameters.Add(new Param(name, defaultValue));
            }
            while (MatchSymbol(","));
        }
        ExpectSymbol(")", "to close the parameters");
        return parameters;
    }

    private Stmt Class()
    {
        var keyword = Advance();
        string name = ExpectName("a class name");
        string? baseName = MatchSymbol(":") ? ExpectName("a base class name") : null;
        ExpectSymbol("{", "to start the class body");
        var methods = new List<FnStmt>();
        while (!MatchSymbol("}"))
        {
            if (Current.Kind == TokenKind.End) throw Error($"missing '}}' for class {name}");
            if (!Current.IsKeyword("fn")) throw Error("a class body holds methods: fn name() { ... }");
            var fn = Advance();
            string method = ExpectName("a method name");
            if (methods.Any(m => m.Path[1] == method)) throw Error($"method '{method}' is defined twice", _tokens[_pos - 1]);
            var (parameters, body) = FunctionRest(fn);
            methods.Add(new FnStmt([name, method], true, parameters, body, fn.Line, fn.Column));
            MatchSymbol(";");
        }
        return new ClassStmt(name, baseName, methods, keyword.Line, keyword.Column);
    }

    private Stmt If(Token start)
    {
        var branches = new List<(Expr, List<Stmt>)>();
        var condition = Expression();
        branches.Add((condition, Block("after the if condition")));
        List<Stmt>? elseBody = null;
        while (true)
        {
            if (MatchKeyword("elif"))
            {
                var c = Expression();
                branches.Add((c, Block("after the elif condition")));
            }
            else if (Current.IsKeyword("else") && PeekAt(1).IsKeyword("if"))
            {
                _pos += 2;
                var c = Expression();
                branches.Add((c, Block("after the else if condition")));
            }
            else if (MatchKeyword("else"))
            {
                elseBody = Block("after else");
                break;
            }
            else break;
        }
        return new IfStmt(branches, elseBody, start.Line, start.Column);
    }

    private Stmt For()
    {
        var keyword = Advance();
        string first = ExpectName("a loop variable");
        string? second = MatchSymbol(",") ? ExpectName("a second loop variable") : null;
        if (!MatchKeyword("in")) throw Error("expected 'in' in for loop: for x in ...");

        var source = Expression();
        if (Current.IsSymbol("..") || Current.IsSymbol("..="))
        {
            if (second != null) throw Error("a range loop has one variable: for i in 1..10");
            bool inclusive = Advance().Text == "..=";
            var to = Expression();
            Expr? step = MatchKeyword("step") ? Expression() : null;
            return new ForRangeStmt(first, source, to, inclusive, step, Block("after the for range"), keyword.Line, keyword.Column);
        }
        return new ForInStmt(first, second, source, Block("after the for source"), keyword.Line, keyword.Column);
    }

    private Stmt On()
    {
        var keyword = Advance();
        string source = ExpectName("an event source (tick, unload, ...)");
        var args = new List<Expr>();
        if (!Current.IsSymbol("(") && !Current.IsSymbol("{"))
        {
            args.Add(Expression());
            while (MatchSymbol(",")) args.Add(Expression());
        }
        var parameters = Current.IsSymbol("(") ? Parameters() : [];
        return new OnStmt(source, args, parameters, Block($"after on {source}"), keyword.Line, keyword.Column);
    }

    // ---------------- Expressions ----------------

    private List<Expr> ExpressionList()
    {
        var list = new List<Expr> { Expression() };
        while (MatchSymbol(",")) list.Add(Expression());
        return list;
    }

    private Expr Expression() => Ternary();

    private Expr Ternary()
    {
        var condition = Pipe();
        if (!Current.IsSymbol("?")) return condition;
        var question = Advance();
        _ternaryDepth++;
        var then = Expression();
        _ternaryDepth--;
        ExpectSymbol(":", "in the ?: expression");
        var otherwise = Expression();
        return new TernaryExpr(condition, then, otherwise, question.Line, question.Column);
    }

    private Expr Pipe()
    {
        var left = Coalesce();
        while (Current.IsSymbol("|>"))
        {
            var op = Advance();
            var right = Coalesce();
            // x |> f(a) == f(x, a);  x |> obj:m(a) == obj:m(x, a);  x |> f == f(x)
            left = right switch
            {
                CallExpr call => call with { Args = [left, .. call.Args] },
                MethodCallExpr method => method with { Args = [left, .. method.Args] },
                _ => new CallExpr(right, [left], op.Line, op.Column),
            };
        }
        return left;
    }

    private Expr Coalesce()
    {
        var left = Binary(0);
        while (Current.IsSymbol("??"))
        {
            var op = Advance();
            left = new CoalesceExpr(left, Binary(0), op.Line, op.Column);
        }
        return left;
    }

    // Binary operator levels, loosest first. Keyword spellings map onto the symbol form.
    private static readonly string[][] Levels =
    [
        ["||", "or"],
        ["&&", "and"],
        ["==", "!="],
        ["<", ">", "<=", ">="],
        ["++"],
        ["+", "-"],
        ["*", "/", "%"],
    ];

    private Expr Binary(int level)
    {
        if (level == Levels.Length) return Unary();
        var left = Binary(level + 1);
        while (Current.Kind is TokenKind.Symbol or TokenKind.Keyword && Levels[level].Contains(Current.Text))
        {
            var op = Advance();
            string symbol = op.Text switch { "or" => "||", "and" => "&&", _ => op.Text };
            var right = Binary(level + 1);
            left = new BinaryExpr(symbol, left, right, op.Line, op.Column);
        }
        return left;
    }

    private Expr Unary()
    {
        var token = Current;
        if (token.IsSymbol("!") || token.IsKeyword("not") || token.IsSymbol("-") || token.IsSymbol("#"))
        {
            Advance();
            string op = token.Text == "not" ? "!" : token.Text;
            return new UnaryExpr(op, Unary(), token.Line, token.Column);
        }
        return Power();
    }

    private Expr Power()
    {
        var left = Postfix();
        if (Current.IsSymbol("**"))
        {
            var op = Advance();
            return new BinaryExpr("**", left, Unary(), op.Line, op.Column); // right-associative
        }
        return left;
    }

    private Expr Postfix()
    {
        var expr = Primary();
        bool callable = expr is not (StringExpr or NumberExpr or LiteralExpr);
        while (true)
        {
            var token = Current;
            if (token.IsSymbol("."))
            {
                Advance();
                string name = Current.Kind is TokenKind.Name or TokenKind.Keyword
                    ? Advance().Text
                    : throw Error($"expected a field name after '.' but found {Describe(Current)}");
                expr = new MemberExpr(expr, name, token.Line, token.Column);
            }
            else if (token.IsSymbol("["))
            {
                Advance();
                var index = Nested(Expression);
                ExpectSymbol("]", "to close the index");
                expr = new IndexExpr(expr, index, token.Line, token.Column);
            }
            else if (token.IsSymbol("(") && callable)
            {
                Advance();
                expr = new CallExpr(expr, Arguments(), token.Line, token.Column);
            }
            else if (token.IsSymbol(":") && _ternaryDepth == 0 && PeekAt(1).Kind is TokenKind.Name or TokenKind.Keyword
                     && PeekAt(2).IsSymbol("("))
            {
                Advance();
                string name = Advance().Text;
                Advance(); // (
                expr = new MethodCallExpr(expr, name, Arguments(), token.Line, token.Column);
            }
            else
            {
                return expr;
            }
            callable = true;
        }
    }

    private T Nested<T>(Func<T> parse)
    {
        int saved = _ternaryDepth;
        _ternaryDepth = 0;
        try
        {
            return parse();
        }
        finally
        {
            _ternaryDepth = saved;
        }
    }

    private List<Expr> Arguments()
    {
        var args = new List<Expr>();
        if (MatchSymbol(")")) return args;
        Nested(() =>
        {
            do
            {
                if (Current.IsSymbol(")")) break; // trailing comma
                args.Add(Expression());
            }
            while (MatchSymbol(","));
            return 0;
        });
        ExpectSymbol(")", "to close the call");
        return args;
    }

    private Expr Primary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new NumberExpr(token.Text, token.Line, token.Column);
            case TokenKind.String:
                Advance();
                return new StringExpr(token.Parts!.Select(ParsePart).ToList(), token.Line, token.Column);
            case TokenKind.Name:
                Advance();
                return new NameExpr(token.Text, token.Line, token.Column);
            case TokenKind.Keyword:
                switch (token.Text)
                {
                    case "true" or "false" or "nil":
                        Advance();
                        return new LiteralExpr(token.Text, token.Line, token.Column);
                    case "fn":
                    {
                        Advance();
                        var (parameters, body) = Nested(() => FunctionRest(token));
                        return new FnExpr(parameters, body, null, token.Line, token.Column);
                    }
                }
                break;
            case TokenKind.Symbol:
                switch (token.Text)
                {
                    case "...":
                        Advance();
                        return new LiteralExpr("...", token.Line, token.Column);
                    case "(":
                    {
                        Advance();
                        var inner = Nested(Expression);
                        ExpectSymbol(")", "to close the parenthesis");
                        // Parentheses around a call keep only its first value, like Lua.
                        return inner is CallExpr or MethodCallExpr or LiteralExpr { Lua: "..." }
                            ? new ParenExpr(inner, token.Line, token.Column)
                            : inner;
                    }
                    case "[":
                        Advance();
                        return Nested(() => List(token));
                    case "{":
                        Advance();
                        return Nested(() => Map(token));
                }
                break;
        }
        throw Error($"expected a value but found {Describe(token)}");
    }

    private object ParsePart(object part)
    {
        if (part is not InterpolationPart p) return part;
        var parser = new EnmaParser(new EnmaLexer(p.Source, p.Line, p.Column).Tokenize());
        var expr = parser.Expression();
        if (parser.Current.Kind != TokenKind.End) throw parser.Error($"unexpected {Describe(parser.Current)} in {{}} of the string");
        return expr;
    }

    private Expr List(Token open)
    {
        var items = new List<Expr>();
        while (!Current.IsSymbol("]"))
        {
            items.Add(Expression());
            if (!MatchSymbol(",")) break;
        }
        ExpectSymbol("]", "to close the list");
        return new ListExpr(items, open.Line, open.Column);
    }

    private Expr Map(Token open)
    {
        var entries = new List<MapEntry>();
        while (!Current.IsSymbol("}"))
        {
            var keyToken = Current;
            if (keyToken.Kind is TokenKind.Name or TokenKind.Keyword && PeekAt(1).IsSymbol(":"))
            {
                _pos += 2;
                entries.Add(new MapEntry(keyToken.Text, null, Expression()));
            }
            else if (keyToken.Kind == TokenKind.Name && (PeekAt(1).IsSymbol(",") || PeekAt(1).IsSymbol("}")))
            {
                // Shorthand {name} == {name: name}
                Advance();
                entries.Add(new MapEntry(keyToken.Text, null, new NameExpr(keyToken.Text, keyToken.Line, keyToken.Column)));
            }
            else if (keyToken.Kind == TokenKind.String && PeekAt(1).IsSymbol(":"))
            {
                var key = Primary();
                Advance(); // :
                entries.Add(new MapEntry(null, key, Expression()));
            }
            else if (keyToken.IsSymbol("["))
            {
                Advance();
                var key = Expression();
                ExpectSymbol("]", "to close the key");
                ExpectSymbol(":", "after the key");
                entries.Add(new MapEntry(null, key, Expression()));
            }
            else
            {
                throw Error("expected a map entry (name: value); for a list use [ ]");
            }
            if (!MatchSymbol(",")) break;
        }
        ExpectSymbol("}", "to close the map");
        return new MapExpr(entries, open.Line, open.Column);
    }
}
