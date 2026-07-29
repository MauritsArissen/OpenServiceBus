namespace OpenServiceBus.Management.Atom.Protocol;

/// <summary>
/// Transport-agnostic HTTP request handed to <see cref="AtomManagementHandler"/>. Both hosting
/// adapters (the broker Host and the embeddable test host) map onto this shape, so the protocol
/// logic has exactly one implementation.
/// </summary>
public sealed record AtomHttpRequest
{
    /// <summary>Upper-case HTTP method (GET/PUT/DELETE).</summary>
    public required string Method { get; init; }

    /// <summary>URL-decoded absolute path, e.g. <c>/orders</c> or <c>/invoices/subscriptions/audit</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Query parameters (<c>api-version</c>, <c>$skip</c>, <c>$top</c>, …), case-insensitive keys.</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>Host</c> header value - used to build entry ids/links.</summary>
    public string HostHeader { get; init; } = "localhost";

    /// <summary>Raw <c>Authorization</c> header, if any.</summary>
    public string? Authorization { get; init; }

    /// <summary>Raw <c>If-Match</c> header - the SDK sends <c>*</c> to signal "update, not create".</summary>
    public string? IfMatch { get; init; }

    /// <summary>Request body (ATOM entry XML) for PUT requests; empty otherwise.</summary>
    public string Body { get; init; } = string.Empty;
}

/// <summary>Response produced by <see cref="AtomManagementHandler"/>.</summary>
public sealed record AtomHttpResponse
{
    public required int StatusCode { get; init; }
    public string ContentType { get; init; } = "application/xml;charset=utf-8";
    public string Body { get; init; } = string.Empty;
}
