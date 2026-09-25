using Cove.Core.Auth;
using Cove.Core.Interfaces;

namespace WhisparrSync.Connection;

/// <summary>Whether registering the callback would lock this Cove down.</summary>
/// <remarks>
/// Asked before this product registers its callback in a Whisparr instance. Whisparr verifies a
/// webhook by posting to it, and that post arrives from wherever Whisparr runs, which for a
/// containerised instance is never Cove's own loopback address.
/// <para>
/// Cove answers such a request in one of two ways. Where an owner account exists and sign-in is
/// off, it reads the request as an instance reachable from outside its own machine: it turns
/// sign-in on, keeps it on, and refuses that request, so the operator is signed out of the Cove
/// they were configuring and the registration fails anyway. Where no owner account exists yet it
/// lets the request through untouched, so first-run setup can be completed from elsewhere.
/// </para>
/// <para>
/// Both halves matter. A product that refused whenever sign-in was off would block the second case,
/// which is a Cove nobody can be locked out of.
/// </para>
/// </remarks>
public interface IHostLockdownPort
{
    /// <summary>Whether a call from outside this machine would lock Cove down.</summary>
    Task<bool> WouldLockDownAsync(CancellationToken ct);
}

/// <summary>Reads the two host facts that decide it.</summary>
/// <remarks>
/// Read on each call rather than captured. Cove writes both at run time, so a value read once at
/// start would answer for a Cove that no longer exists.
/// <para>
/// A host that publishes neither answers <see langword="false"/>, which registers as before. The
/// refusal exists to stop a lockout this product can see coming; where it cannot see the facts it
/// has no grounds to refuse a registration the operator asked for.
/// </para>
/// </remarks>
internal sealed class HostLockdownPort(CoveConfiguration? configuration, IUserService? users)
    : IHostLockdownPort
{
    public async Task<bool> WouldLockDownAsync(CancellationToken ct)
    {
        if (configuration is null || users is null || configuration.Auth.Enabled)
        {
            return false;
        }

        return await users.OwnerExistsAsync(ct).ConfigureAwait(false);
    }
}
