namespace Enma;

public enum EnmaSeverity { Error, Warning }

/// <summary>A compiler message at a 1-based line/column; <see cref="Length"/> is how much to underline.</summary>
public sealed record EnmaDiagnostic(int Line, int Column, int Length, string Message, EnmaSeverity Severity)
{
    public override string ToString() => $"{Line}:{Column}: {(Severity == EnmaSeverity.Error ? "error" : "warning")}: {Message}";
}

/// <summary>Compiled Lua (null when there were errors) plus every error and warning.</summary>
public sealed record EnmaResult(string? Lua, IReadOnlyList<EnmaDiagnostic> Diagnostics)
{
    public bool Success => Lua != null;
    public IEnumerable<EnmaDiagnostic> Errors => Diagnostics.Where(d => d.Severity == EnmaSeverity.Error);
    public IEnumerable<EnmaDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == EnmaSeverity.Warning);
}

/// <summary>Thrown by the lexer/parser to abort on the first syntax error.</summary>
internal sealed class EnmaSyntaxException(EnmaDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public EnmaDiagnostic Diagnostic { get; } = diagnostic;
}

internal enum TokenKind { Name, Keyword, Number, String, Symbol, End }

/// <summary>
/// One token. For strings, <see cref="Parts"/> holds the literal pieces and the embedded
/// <c>{expression}</c> sources of an interpolated string (<see cref="InterpolationPart"/>).
/// </summary>
internal sealed record Token(TokenKind Kind, string Text, int Line, int Column, List<object>? Parts = null)
{
    public bool Is(TokenKind kind, string text) => Kind == kind && Text == text;
    public bool IsSymbol(string text) => Kind == TokenKind.Symbol && Text == text;
    public bool IsKeyword(string text) => Kind == TokenKind.Keyword && Text == text;
}

internal sealed record InterpolationPart(string Source, int Line, int Column);

// ---------------- Syntax tree ----------------

internal abstract record Node(int Line, int Column);

internal abstract record Expr(int Line, int Column) : Node(Line, Column);
internal sealed record NumberExpr(string Text, int Line, int Column) : Expr(Line, Column);
internal sealed record StringExpr(List<object> Parts, int Line, int Column) : Expr(Line, Column); // string | Expr
internal sealed record LiteralExpr(string Lua, int Line, int Column) : Expr(Line, Column);       // true/false/nil/...
internal sealed record NameExpr(string Name, int Line, int Column) : Expr(Line, Column);
internal sealed record MemberExpr(Expr Target, string Name, int Line, int Column) : Expr(Line, Column);
internal sealed record IndexExpr(Expr Target, Expr Index, int Line, int Column) : Expr(Line, Column);
internal sealed record CallExpr(Expr Function, List<Expr> Args, int Line, int Column) : Expr(Line, Column);
internal sealed record MethodCallExpr(Expr Target, string Name, List<Expr> Args, int Line, int Column) : Expr(Line, Column);
internal sealed record BinaryExpr(string Op, Expr Left, Expr Right, int Line, int Column) : Expr(Line, Column);
internal sealed record UnaryExpr(string Op, Expr Operand, int Line, int Column) : Expr(Line, Column);
internal sealed record TernaryExpr(Expr Condition, Expr Then, Expr Else, int Line, int Column) : Expr(Line, Column);
internal sealed record ParenExpr(Expr Inner, int Line, int Column) : Expr(Line, Column);
internal sealed record CoalesceExpr(Expr Left, Expr Right, int Line, int Column) : Expr(Line, Column);
internal sealed record ListExpr(List<Expr> Items, int Line, int Column) : Expr(Line, Column);
internal sealed record MapEntry(string? Name, Expr? Key, Expr Value);
internal sealed record MapExpr(List<MapEntry> Entries, int Line, int Column) : Expr(Line, Column);
internal sealed record FnExpr(List<Param> Params, List<Stmt>? Body, Expr? ExprBody, int Line, int Column) : Expr(Line, Column);

internal abstract record Stmt(int Line, int Column) : Node(Line, Column);
/// <summary>A parameter; <c>...</c> for varargs, optional default value (<c>fn f(a, b = 2)</c>).</summary>
internal sealed record Param(string Name, Expr? Default);
internal sealed record LetStmt(List<string> Names, List<Expr> Values, bool IsConst, int Line, int Column) : Stmt(Line, Column);
internal sealed record AssignStmt(List<Expr> Targets, string Op, List<Expr> Values, int Line, int Column) : Stmt(Line, Column);
internal sealed record ExprStmt(Expr Expr, int Line, int Column) : Stmt(Line, Column);
internal sealed record IfStmt(List<(Expr Condition, List<Stmt> Body)> Branches, List<Stmt>? Else, int Line, int Column) : Stmt(Line, Column);
internal sealed record WhileStmt(Expr Condition, List<Stmt> Body, int Line, int Column) : Stmt(Line, Column);
internal sealed record ForRangeStmt(string Var, Expr From, Expr To, bool Inclusive, Expr? Step, List<Stmt> Body, int Line, int Column) : Stmt(Line, Column);
internal sealed record ForInStmt(string First, string? Second, Expr Source, List<Stmt> Body, int Line, int Column) : Stmt(Line, Column);
internal sealed record ReturnStmt(List<Expr> Values, int Line, int Column) : Stmt(Line, Column);
internal sealed record BreakStmt(int Line, int Column) : Stmt(Line, Column);
internal sealed record ContinueStmt(int Line, int Column) : Stmt(Line, Column);
/// <summary><c>fn name()</c>, <c>fn obj.name()</c> or <c>fn obj:name()</c> (method, gets <c>self</c>).</summary>
internal sealed record FnStmt(List<string> Path, bool IsMethod, List<Param> Params, List<Stmt> Body, int Line, int Column) : Stmt(Line, Column);
internal sealed record ClassStmt(string Name, string? Base, List<FnStmt> Methods, int Line, int Column) : Stmt(Line, Column);
/// <summary><c>on tick { }</c>, <c>on button "click" (e) { }</c>.</summary>
internal sealed record OnStmt(string Source, List<Expr> Args, List<Param> Params, List<Stmt> Body, int Line, int Column) : Stmt(Line, Column);
/// <summary><c>every 0.5 { }</c> / <c>after 2 { }</c>.</summary>
internal sealed record TimerStmt(bool Repeat, Expr Seconds, List<Stmt> Body, int Line, int Column) : Stmt(Line, Column);
