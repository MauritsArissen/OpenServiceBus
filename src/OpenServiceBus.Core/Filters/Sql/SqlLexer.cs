using System.Globalization;
using System.Text;

namespace OpenServiceBus.Core.Filters.Sql;

internal enum SqlTokenKind
{
    Identifier,
    Parameter,
    Number,
    String,
    LeftParen,
    RightParen,
    Comma,
    Dot,
    Semicolon,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Eq,
    NotEq,
    Lt,
    LtEq,
    Gt,
    GtEq,
    KwAnd,
    KwOr,
    KwNot,
    KwIs,
    KwNull,
    KwLike,
    KwEscape,
    KwIn,
    KwExists,
    KwTrue,
    KwFalse,
    EndOfInput,
}

internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, object? Value, int Position);

internal sealed class SqlLexer
{
    private readonly string _source;
    private int _index;

    public SqlLexer(string source)
    {
        _source = source;
    }

    public List<SqlToken> Tokenize()
    {
        var tokens = new List<SqlToken>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == SqlTokenKind.EndOfInput) return tokens;
        }
    }

    private SqlToken NextToken()
    {
        SkipWhitespace();
        if (_index >= _source.Length)
        {
            return new SqlToken(SqlTokenKind.EndOfInput, string.Empty, null, _index);
        }

        var start = _index;
        var ch = _source[_index];

        switch (ch)
        {
            case '(':
                _index++;
                return new SqlToken(SqlTokenKind.LeftParen, "(", null, start);
            case ')':
                _index++;
                return new SqlToken(SqlTokenKind.RightParen, ")", null, start);
            case ',':
                _index++;
                return new SqlToken(SqlTokenKind.Comma, ",", null, start);
            case '.':
                _index++;
                return new SqlToken(SqlTokenKind.Dot, ".", null, start);
            case ';':
                _index++;
                return new SqlToken(SqlTokenKind.Semicolon, ";", null, start);
            case '+':
                _index++;
                return new SqlToken(SqlTokenKind.Plus, "+", null, start);
            case '-':
                _index++;
                return new SqlToken(SqlTokenKind.Minus, "-", null, start);
            case '*':
                _index++;
                return new SqlToken(SqlTokenKind.Star, "*", null, start);
            case '/':
                _index++;
                return new SqlToken(SqlTokenKind.Slash, "/", null, start);
            case '%':
                _index++;
                return new SqlToken(SqlTokenKind.Percent, "%", null, start);
            case '=':
                _index++;
                return new SqlToken(SqlTokenKind.Eq, "=", null, start);
            case '!':
                if (Peek(1) == '=') { _index += 2; return new SqlToken(SqlTokenKind.NotEq, "!=", null, start); }
                throw Error("Unexpected '!'.", start);
            case '<':
                if (Peek(1) == '=') { _index += 2; return new SqlToken(SqlTokenKind.LtEq, "<=", null, start); }
                if (Peek(1) == '>') { _index += 2; return new SqlToken(SqlTokenKind.NotEq, "<>", null, start); }
                _index++;
                return new SqlToken(SqlTokenKind.Lt, "<", null, start);
            case '>':
                if (Peek(1) == '=') { _index += 2; return new SqlToken(SqlTokenKind.GtEq, ">=", null, start); }
                _index++;
                return new SqlToken(SqlTokenKind.Gt, ">", null, start);
            case '\'':
                return ScanString(start);
            case '@':
                return ScanParameter(start);
        }

        if (char.IsDigit(ch))
        {
            return ScanNumber(start);
        }
        if (char.IsLetter(ch) || ch == '_' || ch == '[' || ch == '"')
        {
            return ScanIdentifier(start);
        }

        throw Error($"Unexpected character '{ch}'.", start);
    }

    private SqlToken ScanParameter(int start)
    {
        _index++; // '@'
        var nameStart = _index;
        while (_index < _source.Length && (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_'))
        {
            _index++;
        }
        if (_index == nameStart) throw Error("Expected a parameter name after '@'.", start);
        return new SqlToken(SqlTokenKind.Parameter, _source[nameStart.._index], null, start);
    }

    private SqlToken ScanString(int start)
    {
        _index++; // opening quote
        var sb = new StringBuilder();
        while (_index < _source.Length)
        {
            var c = _source[_index];
            if (c == '\'')
            {
                if (_index + 1 < _source.Length && _source[_index + 1] == '\'')
                {
                    sb.Append('\'');
                    _index += 2;
                    continue;
                }
                _index++;
                return new SqlToken(SqlTokenKind.String, sb.ToString(), sb.ToString(), start);
            }
            sb.Append(c);
            _index++;
        }
        throw Error("Unterminated string literal.", start);
    }

    private SqlToken ScanNumber(int start)
    {
        while (_index < _source.Length && char.IsDigit(_source[_index])) _index++;
        var isReal = false;
        if (_index < _source.Length && _source[_index] == '.')
        {
            isReal = true;
            _index++;
            while (_index < _source.Length && char.IsDigit(_source[_index])) _index++;
        }
        // Scientific notation (approximate_number_constant in the Service Bus grammar):
        // 101.5E5, 0.5E-2. Always a double, matching Azure.
        if (_index < _source.Length && (_source[_index] is 'e' or 'E'))
        {
            var exponentStart = _index;
            _index++;
            if (_index < _source.Length && (_source[_index] is '+' or '-')) _index++;
            if (_index < _source.Length && char.IsDigit(_source[_index]))
            {
                isReal = true;
                while (_index < _source.Length && char.IsDigit(_source[_index])) _index++;
            }
            else
            {
                // Not an exponent after all (e.g. "5east") - back off and let the next
                // token start at the 'e'.
                _index = exponentStart;
            }
        }
        var text = _source[start.._index];
        // Box each branch separately: a `bool ? double : long` conditional TYPES as double,
        // which silently boxed every integer literal as a double and broke long-vs-long
        // equality. Integer constants must be Int64, like the Service Bus grammar says.
        object value;
        if (isReal)
        {
            value = double.Parse(text, CultureInfo.InvariantCulture);
        }
        else
        {
            value = long.Parse(text, CultureInfo.InvariantCulture);
        }
        return new SqlToken(SqlTokenKind.Number, text, value, start);
    }

    private SqlToken ScanIdentifier(int start)
    {
        string identifier;
        if (_source[_index] is '[' or '"')
        {
            // Delimited ([...], with ]] escaping a literal ]) and quoted ("...", with ""
            // escaping) identifiers, per the Service Bus grammar. Either quoting form is
            // always an identifier, never a keyword - that is the whole point: a property
            // literally named "true", "like", "not", etc. stays referenceable.
            var close = _source[_index] == '[' ? ']' : '"';
            _index++;
            var sb = new StringBuilder();
            while (_index < _source.Length)
            {
                var c = _source[_index];
                if (c == close)
                {
                    if (_index + 1 < _source.Length && _source[_index + 1] == close)
                    {
                        sb.Append(close);
                        _index += 2;
                        continue;
                    }
                    _index++;
                    return new SqlToken(SqlTokenKind.Identifier, sb.ToString(), null, start);
                }
                sb.Append(c);
                _index++;
            }
            throw Error(close == ']' ? "Unterminated bracketed identifier." : "Unterminated quoted identifier.", start);
        }

        // Regular identifiers are letter followed by letters/digits/underscores, exactly
        // the Service Bus grammar. Notably NO '-': "total-1" must lex as a subtraction;
        // dashed property names need [brackets].
        while (_index < _source.Length && (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_'))
        {
            _index++;
        }
        identifier = _source[start.._index];

        return identifier.ToUpperInvariant() switch
        {
            "AND" => new SqlToken(SqlTokenKind.KwAnd, identifier, null, start),
            "OR" => new SqlToken(SqlTokenKind.KwOr, identifier, null, start),
            "NOT" => new SqlToken(SqlTokenKind.KwNot, identifier, null, start),
            "IS" => new SqlToken(SqlTokenKind.KwIs, identifier, null, start),
            "NULL" => new SqlToken(SqlTokenKind.KwNull, identifier, null, start),
            "LIKE" => new SqlToken(SqlTokenKind.KwLike, identifier, null, start),
            "ESCAPE" => new SqlToken(SqlTokenKind.KwEscape, identifier, null, start),
            "IN" => new SqlToken(SqlTokenKind.KwIn, identifier, null, start),
            "EXISTS" => new SqlToken(SqlTokenKind.KwExists, identifier, null, start),
            "TRUE" => new SqlToken(SqlTokenKind.KwTrue, identifier, true, start),
            "FALSE" => new SqlToken(SqlTokenKind.KwFalse, identifier, false, start),
            _ => new SqlToken(SqlTokenKind.Identifier, identifier, null, start),
        };
    }

    private void SkipWhitespace()
    {
        while (_index < _source.Length && char.IsWhiteSpace(_source[_index])) _index++;
    }

    private char Peek(int offset) =>
        _index + offset < _source.Length ? _source[_index + offset] : '\0';

    private FormatException Error(string message, int position) =>
        new($"SQL filter parse error at position {position}: {message} (source: \"{_source}\")");
}
