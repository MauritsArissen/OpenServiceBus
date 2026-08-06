using OpenServiceBus.Core.Filters.Sql;

namespace OpenServiceBus.Core.Filters;

/// <summary>
/// A Service Bus SQL rule action: a semicolon-separated list of <c>SET</c>/<c>REMOVE</c>
/// statements that mutate a matched subscription's copy of the message before it lands -
/// e.g. <c>SET sys.Label = 'processed'</c> or <c>SET counter = counter + 1; REMOVE debug</c>.
/// Parsed and validated at rule-creation time; applied per matching subscription during
/// topic fan-out via <see cref="ISqlRuleActionTarget"/>.
/// </summary>
public sealed class SqlRuleAction
{
    public string Expression { get; }

    /// <summary>Parameter values referenced as <c>@name</c> in the action's value
    /// expressions - the SDK's <c>SqlRuleAction.Parameters</c>. Bound at construction.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    private static readonly IReadOnlyDictionary<string, object?> NoParameters =
        new Dictionary<string, object?>();

    private readonly IReadOnlyList<SqlActionStatement> _statements;

    /// <exception cref="FormatException">The expression is not a valid action.</exception>
    public SqlRuleAction(string expression)
        : this(expression, null)
    {
    }

    /// <exception cref="FormatException">The expression is not a valid action.</exception>
    public SqlRuleAction(string expression, IReadOnlyDictionary<string, object?>? parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        Expression = expression;
        Parameters = parameters is { Count: > 0 } ? parameters : NoParameters;
        _statements = SqlActionParser.Parse(expression, Parameters);
    }

    /// <summary>Apply every statement, in order, to the given message target.</summary>
    public void Apply(ISqlRuleActionTarget target)
    {
        foreach (var statement in _statements)
        {
            statement.Apply(target);
        }
    }
}

/// <summary>
/// The mutable message surface a <see cref="SqlRuleAction"/> operates on. Implemented by the
/// wire layer over the actual encoded message; kept abstract here so Core stays
/// protocol-agnostic. <see cref="BuildFilterContext"/> must reflect mutations made so far -
/// statements apply sequentially and later ones read the results of earlier ones.
/// </summary>
public interface ISqlRuleActionTarget
{
    /// <summary>Snapshot of the CURRENT state for expression evaluation.</summary>
    MessageFilterContext BuildFilterContext();

    /// <summary>Set a writable system property (canonical name, e.g. "Subject"). Null clears it.</summary>
    void SetSystemProperty(string canonicalName, string? value);

    void SetApplicationProperty(string name, object? value);

    void RemoveApplicationProperty(string name);
}

/// <summary>
/// Applies a <see cref="SqlRuleAction"/> to an encoded message, producing the mutated copy a
/// subscription receives. Implemented in the AMQP layer (the bytes are AMQP-encoded);
/// consumed by the router during topic fan-out.
/// </summary>
public interface IRuleActionApplier
{
    /// <summary>Return a new encoded message with the action's mutations applied.</summary>
    byte[] Apply(byte[] encodedMessage, SqlRuleAction action);
}
