using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.Shared;

/// <summary>The one seam for running a trusted background DB operation under <see cref="CovePrincipal.System()"/>.</summary>
/// <remarks>
/// A background op (webhook, job, timer) carries whichever principal happened to reach it, or none.
/// Under a present but under-privileged one, CoveContext's per-principal authz query filters return zero
/// rows with no error, silently undercounting a library-wide read; only System bypasses them. A NULL
/// principal bypasses them too, so an absent principal is the safe case and must never stand in for an
/// unprivileged one when proving this. The elevation is reverted in a <c>finally</c>.
/// <para>
/// A request path stays on its caller's principal, because elevating it would bypass per-user authz. A
/// detached body carrying out a user's request still needs the elevation, and re-checks that caller's
/// access explicitly against a snapshot of their principal rather than inferring it from the elevation.
/// Prefer <see cref="RunInSystemScopeAsync{T}(IServiceScopeFactory, Func{IServiceProvider, Task{T}})"/>
/// when the body needs its own scope: it hands out one already elevated.
/// </para>
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

    /// <summary>The void-returning overload - same span + restore contract as the generic form.</summary>
    public static Task RunAsSystemAsync(IServiceProvider scopeServices, Func<Task> body)
        => RunAsSystemAsync(scopeServices, async () =>
        {
            await body();
            return true;
        });

    /// <summary>
    /// Creates a scope from <paramref name="scopes"/>, runs <paramref name="body"/> over that scope's
    /// services already elevated to <see cref="CovePrincipal.System()"/>, and disposes the scope after
    /// the principal is restored.
    /// </summary>
    /// <remarks>
    /// The elevation itself is delegated to <see cref="RunAsSystemAsync{T}"/> rather than repeated, so
    /// there is exactly one place in this module where the principal is swapped and exactly one
    /// <c>finally</c> that puts it back.
    /// </remarks>
    /// <param name="scopes">The scope factory a detached body was handed at initialization.</param>
    /// <param name="body">
    /// The work, receiving the new scope's service provider. It must not outlive the returned task: the
    /// scope - and so any <c>DbContext</c> resolved from it - is disposed when that task completes.
    /// </param>
    public static async Task<T> RunInSystemScopeAsync<T>(
        IServiceScopeFactory scopes, Func<IServiceProvider, Task<T>> body)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await RunAsSystemAsync(scope.ServiceProvider, () => body(scope.ServiceProvider));
    }

    /// <summary>The void-returning form - same scope + elevation + restore contract as the generic one.</summary>
    public static Task RunInSystemScopeAsync(IServiceScopeFactory scopes, Func<IServiceProvider, Task> body)
        => RunInSystemScopeAsync(scopes, async services =>
        {
            await body(services);
            return true;
        });
}
