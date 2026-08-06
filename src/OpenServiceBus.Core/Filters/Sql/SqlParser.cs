namespace OpenServiceBus.Core.Filters.Sql;

/// <summary>
/// Recursive-descent parser for the Service Bus SQL filter subset.
///
/// Grammar (loosely):
///   expression       := or-expr
///   or-expr          := and-expr ( OR and-expr )*
///   and-expr         := unary-expr ( AND unary-expr )*
///   unary-expr       := NOT unary-expr | predicate
///   predicate        := comparison ( IS [NOT] NULL | [NOT] LIKE string [ESCAPE string] | [NOT] IN '(' list ')' )?
///   comparison       := additive ( ( '=' | '!=' | '&lt;&gt;' | '&lt;' | '&lt;=' | '&gt;' | '&gt;=' ) additive )?
///   additive         := multiplicative ( ( '+' | '-' ) multiplicative )*
///   multiplicative   := unary-arith ( ( '*' | '/' | '%' ) unary-arith )*
///   unary-arith      := '-' unary-arith | primary
///   primary          := literal | function-call | property-ref | '(' expression ')' | EXISTS '(' identifier ')' | NOT EXISTS '(' identifier ')'
///   function-call    := NEWID '(' ')' | ( PROPERTY | P ) '(' property-ref | string ')'
///   property-ref     := ( identifier '.' )? identifier
/// </summary>
internal sealed class SqlParser
{
    private readonly List<SqlToken> _tokens;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;
    private int _index;

    public SqlParser(string source, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        _tokens = new SqlLexer(source).Tokenize();
        _parameters = parameters;
    }

    /// <summary>
    /// Parse over a pre-lexed token slice (must end with an <see cref="SqlTokenKind.EndOfInput"/>
    /// token). Used by <see cref="SqlActionParser"/> to parse the value side of a SET statement.
    /// </summary>
    internal SqlParser(List<SqlToken> tokens, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        _tokens = tokens;
        _parameters = parameters;
    }

    public SqlExpressionNode ParseExpression()
    {
        var node = ParseOr();
        if (Current.Kind != SqlTokenKind.EndOfInput)
        {
            throw Error($"Unexpected trailing token '{Current.Text}'.");
        }
        return node;
    }

    private SqlExpressionNode ParseOr()
    {
        var left = ParseAnd();
        while (Match(SqlTokenKind.KwOr))
        {
            var right = ParseAnd();
            left = new SqlOrNode(left, right);
        }
        return left;
    }

    private SqlExpressionNode ParseAnd()
    {
        var left = ParseUnary();
        while (Match(SqlTokenKind.KwAnd))
        {
            var right = ParseUnary();
            left = new SqlAndNode(left, right);
        }
        return left;
    }

    private SqlExpressionNode ParseUnary()
    {
        if (Match(SqlTokenKind.KwNot))
        {
            // NOT EXISTS(prop) is a special form (EXISTS takes a property reference).
            if (Current.Kind == SqlTokenKind.KwExists)
            {
                return ParseExistsAfterKeyword(negate: true);
            }
            var inner = ParseUnary();
            return new SqlNotNode(inner);
        }
        return ParsePredicate();
    }

    private SqlExpressionNode ParsePredicate()
    {
        // EXISTS is a primary on its own.
        if (Current.Kind == SqlTokenKind.KwExists)
        {
            return ParseExistsAfterKeyword(negate: false);
        }

        var left = ParseComparison();

        // IS [NOT] NULL
        if (Match(SqlTokenKind.KwIs))
        {
            var negate = Match(SqlTokenKind.KwNot);
            Expect(SqlTokenKind.KwNull, "Expected NULL after IS.");
            return new SqlIsNullNode(left, negate);
        }

        // [NOT] LIKE / [NOT] IN
        var prefixNegate = Match(SqlTokenKind.KwNot);
        if (Match(SqlTokenKind.KwLike))
        {
            // The Service Bus grammar allows pattern and escape to be any string-valued
            // EXPRESSION. When both are string literals (the overwhelmingly common case)
            // the regex compiles right here, so bad patterns fail at rule creation and
            // evaluation reuses one regex; otherwise they resolve per message.
            var patternNode = ParseAdditive();
            SqlExpressionNode? escapeNode = null;
            if (Match(SqlTokenKind.KwEscape))
            {
                escapeNode = ParseAdditive();
                if (escapeNode is SqlLiteralNode escLit && escLit.Value is string escString && escString.Length != 1)
                {
                    throw Error("ESCAPE requires a single-character string.");
                }
            }
            if (patternNode is SqlLiteralNode patternLit && patternLit.Value is string pattern
                && (escapeNode is null || (escapeNode is SqlLiteralNode el && el.Value is string { Length: 1 })))
            {
                var escapeChar = escapeNode is SqlLiteralNode e ? ((string)e.Value!)[0] : (char?)null;
                try
                {
                    return new SqlLikeNode(left, pattern, escapeChar, prefixNegate);
                }
                catch (FormatException ex)
                {
                    throw Error(ex.Message);
                }
            }
            return new SqlLikeNode(left, patternNode, escapeNode, prefixNegate);
        }
        if (Match(SqlTokenKind.KwIn))
        {
            Expect(SqlTokenKind.LeftParen, "Expected '(' after IN.");
            var values = new List<SqlExpressionNode>();
            while (Current.Kind != SqlTokenKind.RightParen)
            {
                values.Add(ParseAdditive());
                if (!Match(SqlTokenKind.Comma)) break;
            }
            Expect(SqlTokenKind.RightParen, "Expected ')' to close IN list.");
            return new SqlInNode(left, values, prefixNegate);
        }

        if (prefixNegate)
        {
            // The earlier NOT wasn't followed by LIKE/IN; rewind.
            _index--;
            return left;
        }

        return left;
    }

    private SqlExpressionNode ParseComparison()
    {
        var left = ParseAdditive();
        if (TryConsumeComparison(out var op))
        {
            var right = ParseAdditive();
            return new SqlComparisonNode(op, left, right);
        }
        return left;
    }

    private SqlExpressionNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            if (Match(SqlTokenKind.Plus))
            {
                left = new SqlArithmeticNode(SqlArithmeticOp.Add, left, ParseMultiplicative());
            }
            else if (Match(SqlTokenKind.Minus))
            {
                left = new SqlArithmeticNode(SqlArithmeticOp.Subtract, left, ParseMultiplicative());
            }
            else
            {
                return left;
            }
        }
    }

    private SqlExpressionNode ParseMultiplicative()
    {
        var left = ParseUnaryArithmetic();
        while (true)
        {
            if (Match(SqlTokenKind.Star))
            {
                left = new SqlArithmeticNode(SqlArithmeticOp.Multiply, left, ParseUnaryArithmetic());
            }
            else if (Match(SqlTokenKind.Slash))
            {
                left = new SqlArithmeticNode(SqlArithmeticOp.Divide, left, ParseUnaryArithmetic());
            }
            else if (Match(SqlTokenKind.Percent))
            {
                left = new SqlArithmeticNode(SqlArithmeticOp.Modulo, left, ParseUnaryArithmetic());
            }
            else
            {
                return left;
            }
        }
    }

    private SqlExpressionNode ParseUnaryArithmetic()
    {
        if (Match(SqlTokenKind.Minus))
        {
            return new SqlNegateNode(ParseUnaryArithmetic());
        }
        if (Match(SqlTokenKind.Plus))
        {
            return new SqlUnaryPlusNode(ParseUnaryArithmetic());
        }
        return ParsePrimary();
    }

    private SqlExpressionNode ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case SqlTokenKind.LeftParen:
                _index++;
                var inner = ParseOr();
                Expect(SqlTokenKind.RightParen, "Expected ')'.");
                return inner;

            case SqlTokenKind.Number:
            case SqlTokenKind.String:
            case SqlTokenKind.KwTrue:
            case SqlTokenKind.KwFalse:
                _index++;
                return new SqlLiteralNode(token.Value);

            case SqlTokenKind.KwNull:
                _index++;
                return new SqlLiteralNode(null);

            // Parameters bind at parse time (they are per-rule constants supplied via
            // SqlRuleFilter.Parameters / SqlRuleAction.Parameters), so evaluation pays
            // nothing and an undefined parameter fails at rule creation.
            case SqlTokenKind.Parameter:
            {
                _index++;
                if (_parameters is not null
                    && (_parameters.TryGetValue("@" + token.Text, out var parameterValue)
                        || _parameters.TryGetValue(token.Text, out parameterValue)))
                {
                    return new SqlLiteralNode(parameterValue);
                }
                throw Error($"Parameter '@{token.Text}' is not defined. Supply it via the filter's Parameters.");
            }

            case SqlTokenKind.Identifier:
                if (Peek(1).Kind == SqlTokenKind.LeftParen)
                {
                    return ParseFunctionCall();
                }
                return ParsePropertyRef();
        }
        throw Error($"Unexpected token '{token.Text}'.");
    }

    private SqlExpressionNode ParseFunctionCall()
    {
        var nameToken = Current;
        switch (nameToken.Text.ToUpperInvariant())
        {
            case "NEWID":
                _index += 2;
                Expect(SqlTokenKind.RightParen, "newid() takes no arguments.");
                return new SqlNewIdNode();

            case "PROPERTY":
            case "P":
            {
                _index += 2;
                // Fast path: a (possibly scoped) identifier resolves statically. Anything
                // else is, per the grammar, "any valid expression that returns a string" -
                // resolved per message.
                if (Current.Kind == SqlTokenKind.Identifier)
                {
                    var first = Current;
                    _index++;
                    string source = string.Empty;
                    string name;
                    if (Match(SqlTokenKind.Dot))
                    {
                        var second = Current;
                        if (second.Kind != SqlTokenKind.Identifier)
                        {
                            throw Error($"Expected property name after '.' inside {nameToken.Text}(...).");
                        }
                        _index++;
                        source = first.Text;
                        name = second.Text;
                    }
                    else
                    {
                        name = first.Text;
                    }
                    Expect(SqlTokenKind.RightParen, $"Expected ')' to close {nameToken.Text}(...).");
                    return new SqlPropertyRefNode(source, name);
                }

                var nameExpression = ParseAdditive();
                Expect(SqlTokenKind.RightParen, $"Expected ')' to close {nameToken.Text}(...).");
                if (nameExpression is SqlLiteralNode literal)
                {
                    if (literal.Value is not string literalName)
                    {
                        throw Error($"{nameToken.Text}(...) requires a string-valued name.");
                    }
                    return new SqlPropertyRefNode(string.Empty, literalName);
                }
                return new SqlPropertyFunctionNode(nameExpression);
            }

            default:
                throw Error($"Unknown function '{nameToken.Text}'. Supported: newid(), property(name), p(name).");
        }
    }

    private SqlExpressionNode ParsePropertyRef()
    {
        var first = Current;
        _index++;
        if (Match(SqlTokenKind.Dot))
        {
            var second = Current;
            if (second.Kind != SqlTokenKind.Identifier)
            {
                throw Error("Expected property name after '.'.");
            }
            _index++;
            return new SqlPropertyRefNode(first.Text, second.Text);
        }
        return new SqlPropertyRefNode(string.Empty, first.Text);
    }

    private SqlExpressionNode ParseExistsAfterKeyword(bool negate)
    {
        Expect(SqlTokenKind.KwExists, "Expected EXISTS.");
        Expect(SqlTokenKind.LeftParen, "Expected '(' after EXISTS.");
        var firstId = Current;
        if (firstId.Kind != SqlTokenKind.Identifier)
        {
            throw Error("Expected property reference inside EXISTS(...).");
        }
        _index++;
        string source = string.Empty;
        string name;
        if (Match(SqlTokenKind.Dot))
        {
            var second = Current;
            if (second.Kind != SqlTokenKind.Identifier)
            {
                throw Error("Expected property name after '.' inside EXISTS.");
            }
            _index++;
            source = firstId.Text;
            name = second.Text;
        }
        else
        {
            name = firstId.Text;
        }
        Expect(SqlTokenKind.RightParen, "Expected ')' to close EXISTS.");
        return new SqlExistsNode(source, name, negate);
    }

    private bool TryConsumeComparison(out SqlComparisonOp op)
    {
        op = default;
        switch (Current.Kind)
        {
            case SqlTokenKind.Eq: op = SqlComparisonOp.Eq; break;
            case SqlTokenKind.NotEq: op = SqlComparisonOp.NotEq; break;
            case SqlTokenKind.Lt: op = SqlComparisonOp.Lt; break;
            case SqlTokenKind.LtEq: op = SqlComparisonOp.LtEq; break;
            case SqlTokenKind.Gt: op = SqlComparisonOp.Gt; break;
            case SqlTokenKind.GtEq: op = SqlComparisonOp.GtEq; break;
            default: return false;
        }
        _index++;
        return true;
    }

    private SqlToken Current => _tokens[_index];

    private SqlToken Peek(int offset) =>
        _index + offset < _tokens.Count ? _tokens[_index + offset] : _tokens[^1];

    private bool Match(SqlTokenKind kind)
    {
        if (Current.Kind != kind) return false;
        _index++;
        return true;
    }

    private void Expect(SqlTokenKind kind, string message)
    {
        if (Current.Kind != kind) throw Error(message + $" (got '{Current.Text}').");
        _index++;
    }

    private FormatException Error(string message) =>
        new($"SQL filter parse error near '{Current.Text}' at position {Current.Position}: {message}");
}
