using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenServiceBus.Core.Filters.Sql;

/// <summary>
/// Three-valued logic helpers used by SQL filter evaluation. <c>NULL</c> propagates through
/// comparisons (NULL = anything → NULL, NOT NULL → NULL), and <c>NULL</c> in a boolean context
/// is treated as false (matches SB's behavior and SQL's WHERE-clause semantics).
/// </summary>
internal static class SqlEvaluator
{
    /// <summary>Coerce an evaluated value to a boolean for top-level WHERE semantics: NULL → false.</summary>
    public static bool AsBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        _ => throw new InvalidOperationException(
            $"SQL filter top-level expression must be boolean; got {value.GetType().Name}: {value}"),
    };

    public static object? CompareEqual(object? left, object? right)
    {
        if (left is null || right is null) return null;
        return FilterValueComparer.AreEqual(left, right);
    }

    public static object? CompareNotEqual(object? left, object? right) =>
        CompareEqual(left, right) is bool eq ? !eq : (object?)null;

    public static object? CompareOrdered(object? left, object? right, Func<int, bool> compare)
    {
        if (left is null || right is null) return null;
        var l = Normalize(left);
        var r = Normalize(right);
        if (l is double ld && r is double rd) return compare(ld.CompareTo(rd));
        if (l is long li && r is long ri) return compare(li.CompareTo(ri));
        if (l is long lli && r is double rrd) return compare(((double)lli).CompareTo(rrd));
        if (l is double lld && r is long rri) return compare(lld.CompareTo(rri));
        if (l is string ls && r is string rs) return compare(string.CompareOrdinal(ls, rs));
        if (l is DateTimeOffset ldt && r is DateTimeOffset rdt) return compare(ldt.CompareTo(rdt));
        if (l is bool lb && r is bool rb) return compare(lb.CompareTo(rb));
        return null;
    }

    public static object? LogicalAnd(object? left, object? right)
    {
        // Per SQL NULL semantics: TRUE AND NULL = NULL; FALSE AND anything = FALSE.
        if (left is false || right is false) return false;
        if (left is null || right is null) return null;
        return (bool)left && (bool)right;
    }

    public static object? LogicalOr(object? left, object? right)
    {
        // TRUE OR anything = TRUE; FALSE OR NULL = NULL.
        if (left is true || right is true) return true;
        if (left is null || right is null) return null;
        return (bool)left || (bool)right;
    }

    public static object? LogicalNot(object? value) => value switch
    {
        null => null,
        bool b => !b,
        _ => throw new InvalidOperationException("NOT applied to non-boolean value."),
    };

    /// <summary>
    /// Translate a SQL LIKE pattern (with optional <c>ESCAPE</c> character) to a regex.
    /// An escape character makes the FOLLOWING character literal - the standard way to
    /// match a literal <c>%</c> or <c>_</c>. A pattern that ends on a dangling escape is
    /// rejected, mirroring rule-creation validation in Azure.
    /// </summary>
    public static Regex BuildLikeRegex(string pattern, char? escapeChar)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (escapeChar is { } esc && c == esc)
            {
                if (i + 1 >= pattern.Length)
                {
                    throw new FormatException($"LIKE pattern \"{pattern}\" ends with the escape character '{esc}'.");
                }
                i++;
                sb.Append(Regex.Escape(pattern[i].ToString(CultureInfo.InvariantCulture)));
                continue;
            }
            switch (c)
            {
                case '%': sb.Append(".*"); break;
                case '_': sb.Append('.'); break;
                default:
                    sb.Append(Regex.Escape(c.ToString(CultureInfo.InvariantCulture)));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Singleline);
    }

    public static object? MatchIn(object? value, IReadOnlyList<object?> candidates)
    {
        if (value is null) return null;
        foreach (var candidate in candidates)
        {
            if (CompareEqual(value, candidate) is true) return true;
        }
        return false;
    }

    /// <summary>
    /// Arithmetic with SQL NULL propagation. Integers stay integral (long) when both sides
    /// are integral except for division, which follows Service Bus and produces a double.
    /// <c>+</c> doubles as string concatenation when either operand is a string.
    /// </summary>
    public static object? Arithmetic(SqlArithmeticOp op, object? left, object? right)
    {
        if (left is null || right is null) return null;
        var l = Normalize(left);
        var r = Normalize(right);

        if (op == SqlArithmeticOp.Add && (l is string || r is string))
        {
            return Convert.ToString(l, CultureInfo.InvariantCulture) + Convert.ToString(r, CultureInfo.InvariantCulture);
        }

        if (l is long li && r is long ri)
        {
            return op switch
            {
                SqlArithmeticOp.Add => li + ri,
                SqlArithmeticOp.Subtract => li - ri,
                SqlArithmeticOp.Multiply => li * ri,
                SqlArithmeticOp.Divide => ri == 0
                    ? throw new InvalidOperationException("Division by zero in SQL expression.")
                    : (double)li / ri,
                SqlArithmeticOp.Modulo => ri == 0
                    ? throw new InvalidOperationException("Modulo by zero in SQL expression.")
                    : li % ri,
                _ => (object?)null,
            };
        }

        if (l is not (long or double) || r is not (long or double))
        {
            throw new InvalidOperationException(
                $"Arithmetic requires numeric operands; got {l.GetType().Name} and {r.GetType().Name}.");
        }

        var ld = l is long ll ? ll : (double)l;
        var rd = r is long rl ? rl : (double)r;
        return op switch
        {
            SqlArithmeticOp.Add => ld + rd,
            SqlArithmeticOp.Subtract => ld - rd,
            SqlArithmeticOp.Multiply => ld * rd,
            SqlArithmeticOp.Divide => ld / rd,
            SqlArithmeticOp.Modulo => ld % rd,
            _ => (object?)null,
        };
    }

    public static object? Negate(object? value)
    {
        if (value is null) return null;
        return Normalize(value) switch
        {
            long l => -l,
            double d => -d,
            var other => throw new InvalidOperationException($"Unary minus applied to non-numeric value ({other.GetType().Name})."),
        };
    }

    public static object? UnaryPlus(object? value)
    {
        if (value is null) return null;
        return Normalize(value) switch
        {
            long l => l,
            double d => d,
            var other => throw new InvalidOperationException($"Unary plus applied to non-numeric value ({other.GetType().Name})."),
        };
    }

    private static object Normalize(object value) => FilterValueComparer.Normalize(value);

}
