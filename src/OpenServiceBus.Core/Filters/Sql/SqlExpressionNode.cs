namespace OpenServiceBus.Core.Filters.Sql;

internal abstract class SqlExpressionNode
{
    public abstract object? Evaluate(MessageFilterContext message);

    /// <summary>
    /// True when this node produces a boolean result and can therefore stand as the top level
    /// of a filter. Service Bus rejects a filter whose expression is not boolean (e.g. a bare
    /// property or a number) at rule-creation time; we use this to do the same instead of
    /// throwing on every message during evaluation.
    /// </summary>
    public virtual bool ProducesBoolean => false;
}

internal sealed class SqlLiteralNode(object? value) : SqlExpressionNode
{
    private readonly object? _value = value;
    public object? Value => _value;
    public override object? Evaluate(MessageFilterContext message) => _value;
    public override bool ProducesBoolean => _value is bool;
}

internal sealed class SqlPropertyRefNode(string source, string name) : SqlExpressionNode
{
    private readonly string _source = source;
    private readonly string _name = name;
    private readonly bool _isSystem = string.Equals(source, "sys", StringComparison.OrdinalIgnoreCase);

    public string Source => _source;
    public string Name => _name;

    public override object? Evaluate(MessageFilterContext message)
    {
        var resolved = message.TryResolve(_source, _name, out var value);
        if (!resolved && _isSystem)
        {
            // Per the Service Bus docs, evaluating a NONEXISTENT system property is an
            // error (a missing user property is merely unknown). The error surfaces as a
            // non-match for the subscription, never as a failed publish.
            throw new InvalidOperationException($"The system property 'sys.{_name}' does not exist.");
        }
        return value;
    }
}

internal sealed class SqlPropertyFunctionNode(SqlExpressionNode nameExpression) : SqlExpressionNode
{
    public override object? Evaluate(MessageFilterContext message)
    {
        var nameValue = nameExpression.Evaluate(message);
        if (nameValue is null) return null;
        if (nameValue is not string name)
        {
            throw new InvalidOperationException("property(name) requires a string-valued name expression.");
        }
        var dot = name.IndexOf('.');
        string source = string.Empty;
        if (dot > 0)
        {
            var prefix = name[..dot];
            if (prefix.Equals("sys", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                source = prefix;
                name = name[(dot + 1)..];
            }
        }
        message.TryResolve(source, name, out var value);
        return value;
    }
}

internal sealed class SqlAndNode(SqlExpressionNode left, SqlExpressionNode right) : SqlExpressionNode
{
    public override bool ProducesBoolean => true;
    public override object? Evaluate(MessageFilterContext message) =>
        SqlEvaluator.LogicalAnd(left.Evaluate(message), right.Evaluate(message));
}

internal sealed class SqlOrNode(SqlExpressionNode left, SqlExpressionNode right) : SqlExpressionNode
{
    public override bool ProducesBoolean => true;
    public override object? Evaluate(MessageFilterContext message) =>
        SqlEvaluator.LogicalOr(left.Evaluate(message), right.Evaluate(message));
}

internal sealed class SqlNotNode(SqlExpressionNode operand) : SqlExpressionNode
{
    public override bool ProducesBoolean => true;
    public override object? Evaluate(MessageFilterContext message) =>
        SqlEvaluator.LogicalNot(operand.Evaluate(message));
}

internal enum SqlComparisonOp { Eq, NotEq, Lt, LtEq, Gt, GtEq }

internal enum SqlArithmeticOp { Add, Subtract, Multiply, Divide, Modulo }

internal sealed class SqlArithmeticNode(SqlArithmeticOp op, SqlExpressionNode left, SqlExpressionNode right) : SqlExpressionNode
{
    public override object? Evaluate(MessageFilterContext message) =>
        SqlEvaluator.Arithmetic(op, left.Evaluate(message), right.Evaluate(message));
}

internal sealed class SqlNegateNode(SqlExpressionNode operand) : SqlExpressionNode
{
    public override object? Evaluate(MessageFilterContext message) =>
        SqlEvaluator.Negate(operand.Evaluate(message));
}

internal sealed class SqlComparisonNode(SqlComparisonOp op, SqlExpressionNode left, SqlExpressionNode right) : SqlExpressionNode
{
    public override bool ProducesBoolean => true;
    public override object? Evaluate(MessageFilterContext message)
    {
        var l = left.Evaluate(message);
        var r = right.Evaluate(message);
        return op switch
        {
            SqlComparisonOp.Eq => SqlEvaluator.CompareEqual(l, r),
            SqlComparisonOp.NotEq => SqlEvaluator.CompareNotEqual(l, r),
            SqlComparisonOp.Lt => SqlEvaluator.CompareOrdered(l, r, c => c < 0),
            SqlComparisonOp.LtEq => SqlEvaluator.CompareOrdered(l, r, c => c <= 0),
            SqlComparisonOp.Gt => SqlEvaluator.CompareOrdered(l, r, c => c > 0),
            SqlComparisonOp.GtEq => SqlEvaluator.CompareOrdered(l, r, c => c >= 0),
            _ => null,
        };
    }
}

internal sealed class SqlIsNullNode(SqlExpressionNode operand, bool negate) : SqlExpressionNode
{
    public override bool ProducesBoolean => true;
    public override object? Evaluate(MessageFilterContext message)
    {
        var v = operand.Evaluate(message);
        return negate ? v is not null : v is null;
    }
}

internal sealed class SqlLikeNode : SqlExpressionNode
{
    private readonly SqlExpressionNode _operand;
    private readonly bool _negate;
    private readonly System.Text.RegularExpressions.Regex? _staticRegex;
    private readonly SqlExpressionNode? _patternNode;
    private readonly SqlExpressionNode? _escapeNode;

    /// <summary>Literal pattern/escape: the regex compiles here so an invalid pattern
    /// (e.g. a trailing escape character) fails at rule-creation time, and the hot
    /// evaluation path reuses one regex.</summary>
    public SqlLikeNode(SqlExpressionNode operand, string pattern, char? escapeChar, bool negate)
    {
        _operand = operand;
        _negate = negate;
        _staticRegex = SqlEvaluator.BuildLikeRegex(pattern, escapeChar);
    }

    /// <summary>Expression pattern/escape (the Service Bus grammar allows both to be any
    /// string-valued expression, e.g. <c>code LIKE prefix + '%'</c>): resolved per message.</summary>
    public SqlLikeNode(SqlExpressionNode operand, SqlExpressionNode patternNode, SqlExpressionNode? escapeNode, bool negate)
    {
        _operand = operand;
        _negate = negate;
        _patternNode = patternNode;
        _escapeNode = escapeNode;
    }

    public override bool ProducesBoolean => true;

    public override object? Evaluate(MessageFilterContext message)
    {
        var value = _operand.Evaluate(message);
        if (value is null) return null;

        var regex = _staticRegex;
        if (regex is null)
        {
            var patternValue = _patternNode!.Evaluate(message);
            if (patternValue is null) return null;
            if (patternValue is not string pattern)
            {
                throw new InvalidOperationException("LIKE pattern must evaluate to a string.");
            }
            char? escapeChar = null;
            if (_escapeNode is not null)
            {
                var escapeValue = _escapeNode.Evaluate(message);
                if (escapeValue is null) return null;
                if (escapeValue is not string { Length: 1 } escape)
                {
                    throw new InvalidOperationException("LIKE ESCAPE must evaluate to a single-character string.");
                }
                escapeChar = escape[0];
            }
            regex = SqlEvaluator.BuildLikeRegex(pattern, escapeChar);
        }

        var matched = value is string s && regex.IsMatch(s);
        return _negate ? !matched : matched;
    }
}

internal sealed class SqlUnaryPlusNode(SqlExpressionNode operand) : SqlExpressionNode
{
    public override object? Evaluate(MessageFilterContext message) =>
        SqlEvaluator.UnaryPlus(operand.Evaluate(message));
}

internal sealed class SqlNewIdNode : SqlExpressionNode
{
    public override object? Evaluate(MessageFilterContext message) => Guid.NewGuid();
}

internal sealed class SqlInNode(SqlExpressionNode operand, IReadOnlyList<SqlExpressionNode> values, bool negate) : SqlExpressionNode
{
    public override bool ProducesBoolean => true;
    public override object? Evaluate(MessageFilterContext message)
    {
        var v = operand.Evaluate(message);
        var resolved = new object?[values.Count];
        for (var i = 0; i < values.Count; i++) resolved[i] = values[i].Evaluate(message);
        var matched = SqlEvaluator.MatchIn(v, resolved);
        if (matched is null) return null;
        return negate ? !(bool)matched : matched;
    }
}

internal sealed class SqlExistsNode(string source, string name, bool negate) : SqlExpressionNode
{
    public override bool ProducesBoolean => true;
    public override object? Evaluate(MessageFilterContext message)
    {
        // EXISTS is true iff the property is *defined* on the message (not just truthy/non-null).
        // For sys properties we use the resolver and treat a hit as "defined".
        // For user properties, "defined" means key-present in the dictionary, regardless of value.
        // TryResolve is key-presence for user properties (a present-but-null key counts,
        // and lookup is case-insensitive) and known-name for sys properties.
        var exists = message.TryResolve(source, name, out _);
        return negate ? !exists : exists;
    }
}
