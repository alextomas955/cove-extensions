using Cove.Core.Interfaces;

namespace WhisparrSync.Connection;

/// <summary>Whether Cove itself makes a caller authenticate.</summary>
/// <remarks>
/// Asked before this product registers its callback in a Whisparr instance. Whisparr verifies a
/// webhook by posting to it, and that post arrives from wherever Whisparr runs, which for a
/// containerised instance is never the loopback address. A Cove with authentication off treats such
/// a request as an instance exposed beyond its own machine, turns authentication on and keeps it on,
/// which signs the operator out of the Cove they were setting up.
/// <para>
/// So a registration made against an unauthenticated Cove locks the operator out whether or not it
/// succeeds. This product refuses it and says why instead.
/// </para>
/// </remarks>
public interface IHostAuthenticationPort
{
    /// <summary>Whether a caller reaching Cove has to authenticate.</summary>
    bool Required { get; }
}

/// <summary>Reads the host's own authentication switch.</summary>
/// <remarks>
/// Read on each call rather than captured. The host writes this switch at run time, so a value read
/// once at start would answer for a Cove that no longer exists.
/// <para>
/// A host that publishes no configuration answers <see langword="true"/>, which registers as usual.
/// The refusal exists to stop a lockout this product can see coming; where it cannot see the switch
/// at all it has no grounds to refuse a registration the operator asked for.
/// </para>
/// </remarks>
internal sealed class HostAuthenticationPort(CoveConfiguration? configuration) : IHostAuthenticationPort
{
    public bool Required => configuration?.Auth.Enabled ?? true;
}
