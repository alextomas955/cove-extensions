using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.Shared;

/// <summary>The one seam for running a trusted background DB operation under <see cref="CovePrincipal.System()"/>.</summary>
/// <remarks>
/// A background op (webhook / job / timer) has no ambient Cove principal, so its scope reads as Anonymous —
/// under which CoveContext's per-principal authz query filters return ZERO rows with no error, silently
/// undercounting a library-wide read. Only System bypasses those filters. The elevation is reverted in a
/// <c>finally</c>; a request path stays on its caller's principal (elevating it would bypass per-user authz).
/// </remarks>
public static class RunAsSystem
{
    /// <summary>
    /// Resolves the current-principal accessor from <paramref name="scopeServices"/>, sets
    /// <see cref="CovePrincipal.System()"/>, awaits <paramref name="body"/>, and restores the previous
    /// principal even when the body throws. A scope with no accessor runs the body unchanged.
    /// </summary>
    public static async Task<T> RunAsSystemAsync<T>(IServiceProvider scopeServices, Func<Task<T>> body)
    {
        var principals = scopeServices.GetService<ICurrentPrincipalAccessor>();
        var previousPrincipal = principals?.Current;
        principals?.Set(CovePrincipal.System());
        try
        {
            return await body();
        }
        finally
        {
            principals?.Set(previousPrincipal);
        }
    }

    /// <summary>The void-returning overload — same span + restore contract as the generic form.</summary>
    public static Task RunAsSystemAsync(IServiceProvider scopeServices, Func<Task> body)
        => RunAsSystemAsync(scopeServices, async () =>
        {
            await body();
            return true;
        });
}
