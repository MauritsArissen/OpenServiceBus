namespace OpenServiceBus.Core.Filters;

/// <summary>
/// Value normalization and cross-type equality shared by the SQL evaluator and
/// <see cref="CorrelationFilter"/>. Every integral type widens to long and every
/// fractional type to double, so cross-type compare and arithmetic work. The unsigned
/// types matter for cross-SDK parity - rhea (the JS SDK) encodes plain numbers as
/// AMQP uint/ulong.
/// </summary>
internal static class FilterValueComparer
{
    public static object Normalize(object value) => value switch
    {
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        sbyte sb => (long)sb,
        ushort us => (long)us,
        uint ui => (long)ui,
        ulong ul => ul <= long.MaxValue ? (long)ul : (double)ul,
        float f => (double)f,
        decimal d => (double)d,
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
        _ => value,
    };

    /// <summary>
    /// Cross-type numeric equality follows C# implicit conversion semantics like the
    /// real service: 7 = 7.0 is true even though boxed Equals(long, double) is not.
    /// </summary>
    public static bool AreEqual(object left, object right)
    {
        var l = Normalize(left);
        var r = Normalize(right);
        if (l is long ll && r is double rd) return (double)ll == rd;
        if (l is double ld && r is long rl) return ld == (double)rl;
        return Equals(l, r);
    }
}
