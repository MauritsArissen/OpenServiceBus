namespace OpenServiceBus.Core.Filters;

/// <summary>
/// Subset of Service Bus's SQL-92-like filter expression language. Supports:
///   • Property references: <c>sys.Subject</c>, <c>user.region</c>, bare <c>region</c>
///   • Comparisons: <c>=</c>, <c>!=</c> or <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>
///   • Logical: <c>AND</c>, <c>OR</c>, <c>NOT</c>, parentheses
///   • <c>IS NULL</c>, <c>IS NOT NULL</c>
///   • <c>LIKE 'pattern'</c> with <c>%</c> and <c>_</c> wildcards
///   • <c>IN (a, b, c)</c>, <c>NOT IN (...)</c>
///   • <c>EXISTS(propertyName)</c>, <c>NOT EXISTS(propertyName)</c>
///   • Literals: strings (single quotes, with <c>''</c> escape), integers, decimals, <c>TRUE</c>/<c>FALSE</c>/<c>NULL</c>
///
/// Out of scope (deferred): date arithmetic, <c>newid()</c>, parameterised filters,
/// concatenation/numeric operators on expressions.
/// </summary>
public sealed class SqlFilter : RuleFilter
{
    public string Expression { get; }

    private readonly Sql.SqlExpressionNode _root;

    public SqlFilter(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        Expression = expression;
        _root = new Sql.SqlParser(expression).ParseExpression();

        // Reject non-boolean expressions here, at construction, the way Service Bus rejects them
        // at rule-creation time. Otherwise a filter like "1" or "user.count" would parse fine and
        // then throw on the publish hot path for every message the rule is evaluated against.
        if (!_root.ProducesBoolean)
        {
            throw new ArgumentException(
                $"SQL filter expression must evaluate to a boolean: \"{expression}\".", nameof(expression));
        }
    }

    public override bool Matches(MessageFilterContext message) =>
        Sql.SqlEvaluator.AsBool(_root.Evaluate(message));
}
