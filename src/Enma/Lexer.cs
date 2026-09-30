using System.Text;

namespace Enma;

/// <summary>
/// Turns Enma source into tokens. Comments are <c>// line</c> and <c>/* block */</c>. Double-quoted
/// strings interpolate <c>{expr}</c> (write <c>\{</c> for a literal brace); single-quoted strings don't.
/// <paramref name="baseLine"/>/<paramref name="baseColumn"/> place the tokens of an interpolated
/// expression at their real position in the file.
/// </summary>
internal sealed class EnmaLexer(string source, int baseLine = 1, int baseColumn = 1)
{
    public static readonly HashSet<string> Keywords =
    [
        "let", "const", "fn", "return", "if", "elif", "else", "while", "for", "in", "step", "break",
        "continue", "true", "false", "nil", "and", "or", "not", "class", "on", "every", "after",
    ];

    // Longest first so "..=" wins over ".." and "." .
    private static readonly string[] Symbols =
    [
        "..=", "++=", "...",
        "==", "!=", "<=", ">=", "&&", "||", "++", "**", "+=", "-=", "*=", "/=", "%=", "|>", "??", "=>", "..",
        "+", "-", "*", "/", "%", "<", ">", "=", "!", "(", ")", "{", "}", "[", "]", ",", ".", ":", ";", "?", "#",
    ];

    private int _pos;
    private int _line = baseLine;
    private int _column = baseColumn;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipTrivia();
            if (_pos >= source.Length)
            {
                tokens.Add(new Token(TokenKind.End, "", _line, _column));
                return tokens;
            }
            tokens.Add(Next());
        }
    }

    private char Peek(int offset = 0) => _pos + offset < source.Length ? source[_pos + offset] : '\0';

    private void Advance()
    {
        if (source[_pos] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        _pos++;
    }

    private EnmaSyntaxException Error(string message, int line, int column, int length = 1) =>
        new(new EnmaDiagnostic(line, column, length, message, EnmaSeverity.Error));

    private void SkipTrivia()
    {
        while (_pos < source.Length)
        {
            char c = Peek();
            if (char.IsWhiteSpace(c))
            {
                Advance();
            }
            else if (c == '/' && Peek(1) == '/')
            {
                while (_pos < source.Length && Peek() != '\n') Advance();
            }
            else if (c == '/' && Peek(1) == '*')
            {
                int line = _line, column = _column;
                Advance();
                Advance();
                while (!(Peek() == '*' && Peek(1) == '/'))
                {
                    if (_pos >= source.Length) throw Error("unclosed /* comment", line, column, 2);
                    Advance();
                }
                Advance();
                Advance();
            }
            else
            {
                return;
            }
        }
    }

    private Token Next()
    {
        int line = _line, column = _column;
        char c = Peek();

        if (char.IsLetter(c) || c == '_')
        {
            int start = _pos;
            while (char.IsLetterOrDigit(Peek()) || Peek() == '_') Advance();
            string word = source[start.._pos];
            return new Token(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Name, word, line, column);
        }

        if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1))))
            return Number(line, column);

        if (c == '"' || c == '\'')
            return String(c, line, column);

        foreach (string symbol in Symbols)
        {
            if (string.CompareOrdinal(source, _pos, symbol, 0, symbol.Length) != 0) continue;
            // "1..5" must lex as 1 .. 5, handled in Number; here ".." is always the range operator.
            for (int i = 0; i < symbol.Length; i++) Advance();
            return new Token(TokenKind.Symbol, symbol, line, column);
        }

        throw Error($"unexpected character '{c}'", line, column);
    }

    private Token Number(int line, int column)
    {
        int start = _pos;
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            Advance();
            Advance();
            while (Uri.IsHexDigit(Peek()) || Peek() == '_') Advance();
        }
        else
        {
            while (char.IsDigit(Peek()) || Peek() == '_') Advance();
            // A fraction, but not the start of a range: 1..10
            if (Peek() == '.' && Peek(1) != '.' && char.IsDigit(Peek(1)))
            {
                Advance();
                while (char.IsDigit(Peek()) || Peek() == '_') Advance();
            }
            if (Peek() is 'e' or 'E' && (char.IsDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsDigit(Peek(2)))))
            {
                Advance();
                if (Peek() is '+' or '-') Advance();
                while (char.IsDigit(Peek())) Advance();
            }
        }
        if (char.IsLetter(Peek()) || Peek() == '_') throw Error("malformed number", line, column, _pos - start + 1);
        return new Token(TokenKind.Number, source[start.._pos].Replace("_", ""), line, column);
    }

    private Token String(char quote, int line, int column)
    {
        bool interpolate = quote == '"';
        var parts = new List<object>();
        var text = new StringBuilder();
        Advance(); // opening quote

        while (true)
        {
            if (_pos >= source.Length) throw Error("unclosed string", line, column);
            char c = Peek();
            if (c == quote)
            {
                Advance();
                break;
            }
            if (c == '\\')
            {
                int escLine = _line, escColumn = _column;
                Advance();
                if (_pos >= source.Length) throw Error("unclosed string", line, column);
                char e = Peek();
                Advance();
                text.Append(e switch
                {
                    'n' => "\n", 't' => "\t", 'r' => "\r", '0' => "\0", '\\' => "\\", '"' => "\"", '\'' => "'",
                    '{' => "{", '}' => "}",
                    _ => throw Error($"unknown escape '\\{e}'", escLine, escColumn, 2),
                });
                continue;
            }
            if (interpolate && c == '{')
            {
                if (text.Length > 0)
                {
                    parts.Add(text.ToString());
                    text.Clear();
                }
                int exprLine = _line, exprColumn = _column + 1;
                Advance();
                int start = _pos, depth = 0;
                while (true)
                {
                    if (_pos >= source.Length || Peek() == '\n') throw Error("unclosed { in string", exprLine, exprColumn - 1);
                    char d = Peek();
                    if (d == '{') depth++;
                    else if (d == '}' && depth-- == 0) break;
                    else if (d == '\'' ) // a quoted string inside the expression: skip it whole
                    {
                        Advance();
                        while (_pos < source.Length && Peek() != '\'' && Peek() != '\n') Advance();
                    }
                    Advance();
                }
                string expr = source[start.._pos];
                Advance(); // closing brace
                if (expr.Trim().Length == 0) throw Error("empty {} in string", exprLine, exprColumn - 1, 2);
                parts.Add(new InterpolationPart(expr, exprLine, exprColumn));
                continue;
            }
            text.Append(c);
            Advance();
        }

        if (text.Length > 0 || parts.Count == 0) parts.Add(text.ToString());
        return new Token(TokenKind.String, "", line, column, parts);
    }
}
