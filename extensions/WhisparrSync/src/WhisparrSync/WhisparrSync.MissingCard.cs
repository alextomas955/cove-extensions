using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    /// <summary>The longest provider identifier this product will put in an outbound body.</summary>
    /// <remarks>
    /// The identifier is the one value on this surface a caller names, so it is bounded before it
    /// reaches a request. Both providers issue identifiers far inside this: one a UUID, the other a
    /// decimal integer.
    /// </remarks>
    private const int ProviderSceneIdBound = 128;

    /// <summary>Marks one catalogue scene as wanted, without acquiring it.</summary>
    /// <remarks>
    /// Composes the newer generation's scene add, whose acquisition-suppressing flag is set from the
    /// one constant every non-grabbing body reads. The instance is asked to look for nothing.
    /// </remarks>
    internal static async Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        MonitorMissingSceneAsync(
            string kind,
            int coveId,
            string providerSceneId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(kind, coveId, out _) || !IsBoundedSceneId(providerSceneId))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        // A generation registering no scene add has no implementation to hand over, so there is
        // nothing to compose and nothing was sent. The vocabulary carries no capability value, and
        // this one is true of what happened rather than of why.
        if (target.Capabilities.Obtain<IWhisparrMissingSceneActing>()
                .Match<IWhisparrMissingSceneActing?>(held => held, _ => null)
            is not { } acting)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.InstanceRefused));
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(target.BaseAddress, target.ApiKey, ct),
                target,
                log,
                ct).ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        var defaults = AddDefaultsProjector.From(profiles.Body, roots.Body);
        if (defaults.Defaults is not { } composeWith)
        {
            return TypedResults.Ok(NothingWasSent(ActionRefusalFor(defaults.Refusal)));
        }

        var added = await ContainedAsync(
            () => acting.AddSceneAsync(
                target.BaseAddress, target.ApiKey, providerSceneId, composeWith, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        if (added is null)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        return TypedResults.Ok(
            MonitoringProjector.Accepted(added) is MonitorRefusalKind.None
                ? new MissingSceneActionResult(
                    MissingSceneState.Monitored, MissingSceneActionRefusal.None)
                : NothingWasSent(MissingSceneActionRefusal.InstanceRefused));
    }

    /// <summary>Asks the connected instance to look for one catalogue scene.</summary>
    /// <remarks>The one verb on this surface that can make an instance download.</remarks>
    internal static Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        SearchMissingSceneAsync(
            string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal)
        => Task.FromResult(MissingSceneAction(kind, coveId, providerSceneId, principal));

    private static Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode> MissingSceneAction(
        string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(kind, coveId, out _) || !IsBoundedSceneId(providerSceneId))
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
    }

    /// <summary>An answer claiming nothing about the instance, and the reason it claims nothing.</summary>
    private static MissingSceneActionResult NothingWasSent(MissingSceneActionRefusal refusal)
        => new(MissingSceneState.StatusUnknown, refusal);

    /// <summary>The card's own vocabulary for a value an add could not be composed without.</summary>
    private static MissingSceneActionRefusal ActionRefusalFor(MonitorRefusalKind refusal)
        => refusal switch
        {
            MonitorRefusalKind.NoQualityProfile
                => MissingSceneActionRefusal.InstanceOffersNoQualityProfile,
            MonitorRefusalKind.NoRootFolder
                => MissingSceneActionRefusal.InstanceOffersNoRootFolder,
            _ => MissingSceneActionRefusal.InstanceRefused,
        };

    /// <summary>
    /// Whether <paramref name="providerSceneId"/> is inside what this product will send on.
    /// </summary>
    /// <remarks>
    /// A control character would reach a JSON body and a header-shaped answer's parser, and neither
    /// provider issues one, so the bound is on the shape rather than on either provider's spelling.
    /// </remarks>
    private static bool IsBoundedSceneId(string providerSceneId)
        => !string.IsNullOrWhiteSpace(providerSceneId)
            && providerSceneId.Length <= ProviderSceneIdBound
            && !providerSceneId.Any(char.IsControl)
            && providerSceneId.Trim().Length == providerSceneId.Length;
}
