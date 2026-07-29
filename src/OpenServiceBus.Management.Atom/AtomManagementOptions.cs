namespace OpenServiceBus.Management.Atom;

/// <summary>
/// Behaviour knobs for the ATOM management surface.
/// </summary>
public sealed class AtomManagementOptions
{
    /// <summary>
    /// Authorization callback invoked with the raw <c>Authorization</c> header of every request
    /// (a <c>SharedAccessSignature …</c> token when the Azure SDK is the caller). Return false to
    /// reject with 401. Null (the default) preserves emulator-mode permissive auth: every
    /// request is accepted, mirroring the AMQP listener's default <c>$cbs</c> behaviour.
    /// </summary>
    public Func<string?, bool>? AuthorizeRequest { get; set; }

    /// <summary>Largest request body accepted, in bytes. ATOM entity descriptions are tiny.</summary>
    public int MaxRequestBodyBytes { get; set; } = 1024 * 1024;
}
