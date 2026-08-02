using System.Globalization;

namespace OpenServiceBus.Core.Filters.Sql;

/// <summary>
/// Parser for the Service Bus SQL rule ACTION grammar - a semicolon-separated list of
/// mutation statements over the same expression language the filters use:
///
///   action     := statement ( ';' statement )* ( ';' )?
///   statement  := SET property-ref '=' additive-expression
///               | REMOVE property-ref
///
/// <c>SET sys.X</c> is limited to the system properties Service Bus lets an action write
/// (Label/Subject, CorrelationId, To, ReplyTo, ReplyToSessionId, ContentType);
/// <c>REMOVE</c> applies to application properties only. Everything else is rejected at
/// rule-creation time, mirroring how filters reject non-boolean expressions.
/// </summary>
internal static class SqlActionParser
{
    public static IReadOnlyList<SqlActionStatement> Parse(string expression)
    {
        var tokens = new SqlLexer(expression).Tokenize();
        var statements = new List<SqlActionStatement>();
        var index = 0;

        while (tokens[index].Kind != SqlTokenKind.EndOfInput)
        {
            if (tokens[index].Kind == SqlTokenKind.Semicolon)
            {
                index++;
                continue;
            }

            var keyword = tokens[index];
            if (keyword.Kind != SqlTokenKind.Identifier)
            {
                throw Error(expression, keyword, "Expected SET or REMOVE.");
            }
            index++;

            if (keyword.Text.Equals("SET", StringComparison.OrdinalIgnoreCase))
            {
                var (isSystem, name) = ParsePropertyRef(expression, tokens, ref index, forRemove: false);
                if (tokens[index].Kind != SqlTokenKind.Eq)
                {
                    throw Error(expression, tokens[index], "Expected '=' after the SET target.");
                }
                index++;
                var value = ParseValueExpression(expression, tokens, ref index);
                statements.Add(new SqlSetStatement(isSystem, name, value));
            }
            else if (keyword.Text.Equals("REMOVE", StringComparison.OrdinalIgnoreCase))
            {
                var (_, name) = ParsePropertyRef(expression, tokens, ref index, forRemove: true);
                statements.Add(new SqlRemoveStatement(name));
            }
            else
            {
                throw Error(expression, keyword, $"Expected SET or REMOVE, got '{keyword.Text}'.");
            }

            if (tokens[index].Kind is not (SqlTokenKind.Semicolon or SqlTokenKind.EndOfInput))
            {
                throw Error(expression, tokens[index], "Expected ';' between statements.");
            }
        }

        if (statements.Count == 0)
        {
            throw new FormatException($"SQL rule action contains no statements: \"{expression}\".");
        }
        return statements;
    }

    private static (bool IsSystem, string Name) ParsePropertyRef(
        string expression, List<SqlToken> tokens, ref int index, bool forRemove)
    {
        var first = tokens[index];
        if (first.Kind != SqlTokenKind.Identifier)
        {
            throw Error(expression, first, "Expected a property reference.");
        }
        index++;

        string source = string.Empty;
        string name = first.Text;
        if (tokens[index].Kind == SqlTokenKind.Dot)
        {
            index++;
            var second = tokens[index];
            if (second.Kind != SqlTokenKind.Identifier)
            {
                throw Error(expression, second, "Expected a property name after '.'.");
            }
            index++;
            source = first.Text;
            name = second.Text;
        }

        switch (source.ToLowerInvariant())
        {
            case "":
            case "user":
                return (false, name);
            case "sys":
                if (forRemove)
                {
                    throw new FormatException(
                        $"REMOVE cannot target system properties (sys.{name}); use SET sys.{name} = NULL. Action: \"{expression}\".");
                }
                return (true, CanonicalWritableSystemProperty(expression, name));
            default:
                throw new FormatException(
                    $"Unknown property source '{source}' in SQL rule action \"{expression}\"; use sys.X, user.X, or a bare name.");
        }
    }

    /// <summary>The system properties a Service Bus SQL rule action may write.</summary>
    private static string CanonicalWritableSystemProperty(string expression, string name) =>
        name.ToLowerInvariant() switch
        {
            "label" or "subject" => "Subject",
            "correlationid" => "CorrelationId",
            "to" => "To",
            "replyto" => "ReplyTo",
            "replytosessionid" => "ReplyToSessionId",
            "contenttype" => "ContentType",
            _ => throw new FormatException(
                $"System property 'sys.{name}' cannot be set by a rule action. " +
                $"Writable: sys.Label, sys.CorrelationId, sys.To, sys.ReplyTo, sys.ReplyToSessionId, sys.ContentType. " +
                $"Action: \"{expression}\"."),
        };

    private static SqlExpressionNode ParseValueExpression(string expression, List<SqlToken> tokens, ref int index)
    {
        var slice = new List<SqlToken>();
        var depth = 0;
        while (true)
        {
            var token = tokens[index];
            if (token.Kind == SqlTokenKind.EndOfInput) break;
            if (token.Kind == SqlTokenKind.Semicolon && depth == 0) break;
            if (token.Kind == SqlTokenKind.LeftParen) depth++;
            if (token.Kind == SqlTokenKind.RightParen) depth--;
            slice.Add(token);
            index++;
        }
        if (slice.Count == 0)
        {
            throw Error(expression, tokens[index], "Expected a value expression after '='.");
        }
        slice.Add(new SqlToken(SqlTokenKind.EndOfInput, string.Empty, null, tokens[index].Position));
        return new SqlParser(slice).ParseExpression();
    }

    private static FormatException Error(string expression, SqlToken token, string message) =>
        new($"SQL rule action parse error near '{token.Text}' at position {token.Position}: {message} (action: \"{expression}\")");
}

/// <summary>One parsed statement of a SQL rule action, applied in order.</summary>
internal abstract class SqlActionStatement
{
    public abstract void Apply(ISqlRuleActionTarget target);
}

internal sealed class SqlSetStatement(bool isSystem, string name, SqlExpressionNode value) : SqlActionStatement
{
    public override void Apply(ISqlRuleActionTarget target)
    {
        var evaluated = value.Evaluate(target.BuildFilterContext());
        if (isSystem)
        {
            target.SetSystemProperty(name, evaluated is null ? null : Convert.ToString(evaluated, CultureInfo.InvariantCulture));
        }
        else
        {
            target.SetApplicationProperty(name, evaluated);
        }
    }
}

internal sealed class SqlRemoveStatement(string name) : SqlActionStatement
{
    public override void Apply(ISqlRuleActionTarget target) => target.RemoveApplicationProperty(name);
}
