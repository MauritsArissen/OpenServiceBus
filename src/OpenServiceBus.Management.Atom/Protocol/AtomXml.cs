using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace OpenServiceBus.Management.Atom.Protocol;

/// <summary>
/// The XML vocabulary of the Service Bus ATOM-pub management protocol: namespaces, the
/// entry/feed envelopes, and the primitive value formats (ISO-8601 durations, UTC timestamps)
/// the official SDK parsers expect.
/// </summary>
public static class AtomXml
{
    public static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    /// <summary>Namespace of every entity description element (QueueDescription, TopicDescription, …).</summary>
    public static readonly XNamespace Sb = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";

    /// <summary>Namespace of the MessageCountDetails children (ActiveMessageCount, …).</summary>
    public static readonly XNamespace CountDetails = "http://schemas.microsoft.com/netservices/2011/06/servicebus";

    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    public static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

    public const string EntryContentType = "application/atom+xml;type=entry;charset=utf-8";
    public const string FeedContentType = "application/atom+xml;type=feed;charset=utf-8";

    /// <summary>
    /// Durations at or above this are "effectively unlimited" - Azure models an absent TTL as
    /// <see cref="TimeSpan.MaxValue"/> (<c>P10675199DT2H48M5.4775807S</c> on the wire), and the
    /// SDK's option types default to it. Parsed values this large map back to null.
    /// </summary>
    private static readonly TimeSpan UnlimitedThreshold = TimeSpan.FromDays(365 * 99);

    public static string Duration(TimeSpan value) => XmlConvert.ToString(value);

    /// <summary>Null (no TTL configured) serializes as TimeSpan.MaxValue, matching Azure.</summary>
    public static string DurationOrUnlimited(TimeSpan? value) => XmlConvert.ToString(value ?? TimeSpan.MaxValue);

    public static TimeSpan ParseDuration(string text) => XmlConvert.ToTimeSpan(text);

    /// <summary>Parse a duration where "effectively infinite" collapses to null.</summary>
    public static TimeSpan? ParseOptionalDuration(string text)
    {
        var value = XmlConvert.ToTimeSpan(text);
        return value >= UnlimitedThreshold ? null : value;
    }

    public static string Timestamp(DateTimeOffset value) =>
        XmlConvert.ToString(value.UtcDateTime, XmlDateTimeSerializationMode.Utc);

    /// <summary>Wrap an entity description in the ATOM entry envelope the SDK parsers unwrap.</summary>
    public static XElement Entry(
        string baseUrl,
        string selfPath,
        string title,
        DateTimeOffset published,
        DateTimeOffset updated,
        XElement description)
    {
        return new XElement(Atom + "entry",
            new XElement(Atom + "id", $"{baseUrl}/{selfPath}"),
            new XElement(Atom + "title", new XAttribute("type", "text"), title),
            new XElement(Atom + "published", Timestamp(published)),
            new XElement(Atom + "updated", Timestamp(updated)),
            new XElement(Atom + "author", new XElement(Atom + "name", "OpenServiceBus")),
            new XElement(Atom + "link", new XAttribute("rel", "self"), new XAttribute("href", selfPath)),
            new XElement(Atom + "content",
                new XAttribute("type", "application/xml"),
                description));
    }

    /// <summary>Wrap a page of entries in the ATOM feed envelope used by list operations.</summary>
    public static XElement Feed(string baseUrl, string title, DateTimeOffset updated, IEnumerable<XElement> entries)
    {
        return new XElement(Atom + "feed",
            new XElement(Atom + "title", new XAttribute("type", "text"), title),
            new XElement(Atom + "id", $"{baseUrl}/$Resources/{title}"),
            new XElement(Atom + "updated", Timestamp(updated)),
            entries);
    }

    /// <summary>The error body shape the SDK surfaces as the exception message detail.</summary>
    public static string ErrorBody(int statusCode, string detail) =>
        new XElement("Error",
            new XElement("Code", statusCode.ToString(CultureInfo.InvariantCulture)),
            new XElement("Detail", detail)).ToString(SaveOptions.DisableFormatting);

    public static string Serialize(XElement root) =>
        // The SDK's XDocument.Parse accepts a bare root element; the declaration marks the
        // charset explicitly for non-SDK consumers.
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + root.ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// Unwrap the description element (QueueDescription, RuleDescription, …) from an incoming
    /// ATOM entry body. The SDK always PUTs <c>entry/content/{Description}</c>; a bare
    /// description element is tolerated for hand-rolled clients.
    /// </summary>
    public static XElement? UnwrapDescription(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var root = XDocument.Parse(body).Root;
        if (root is null) return null;
        if (root.Name.Namespace == Sb) return root;
        if (root.Name != Atom + "entry") return null;
        return root.Element(Atom + "content")?.Elements().FirstOrDefault(e => e.Name.Namespace == Sb);
    }
}
