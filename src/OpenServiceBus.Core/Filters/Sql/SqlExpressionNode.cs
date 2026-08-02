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
    public override object? Evaluate(MessageFilterContext message) => _value;
    public override bool ProducesBoolean => _value is bool;
}

internal sealed class SqlPropertyRefNode(string source, string name) : SqlExpressionNode
{
    private readonly string _source = source;
    private readonly string _name = name;

    public string Source => _source;
    public string Name => _name;

    public override object? Evaluate(MessageFilterContext message)
    {
        message.TryResolve(_source, _name, out var value);
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

internal sealed class SqlLikeNode(SqlExpressionNode operand, string pattern, bool negate) : SqlExpressionNode
{
    public override bool ProducesBoolean => true;
    public override object? Evaluate(MessageFilterContext message)
    {
        var matched = SqlEvaluator.MatchLike(operand.Evaluate(message), pattern);
        if (matched is null) return null;
        return negate ? !(bool)matched : matched;
    }
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
        bool exists;
        if (string.Equals(source, "user", StringComparison.OrdinalIgnoreCase) || source.Length == 0)
        {
            exists = message.ApplicationProperties.ContainsKey(name);
        }
        else
        {
            exists = message.TryResolve(source, name, out _);
        }
        return negate ? !exists : exists;
    }
}
