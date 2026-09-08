using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    /// <summary>What the connected instance holds for each card on one rendered page.</summary>
    /// <remarks>
    /// Reads and changes nothing. It reaches no metadata provider either: the identifier each card is
    /// named by is the library's own stored row, so there is no lookup to make.
    /// <para>
    /// One row per requested identifier, in the order requested. The answer's size is the caller's
    /// own and never the library's, and there is no cap that truncates: a body over the bound is
    /// refused rather than served short.
    /// </para>
    /// <para>
    /// The read tier, <see cref="ReadPermissions"/>: the route composes no write, and a caller who
    /// may see the library may see a read-only status over it.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<LibraryStatusView>, BadRequest, ForbiddenCode>>
        ReadLibraryStatusAsync(
            string kind,
            LibraryStatusRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryStatusPort cards,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        // The cap is one rendered page, which is the same figure Cove's own list pages default their
        // perPage to. Referenced rather than restated, so the two cannot drift.
        if (!TryReadCardKind(kind, out var entityKind)
            || request is not { CoveIds.Count: > 0 }
            || request.CoveIds.Count > MissingPerPage
            || request.CoveIds.Any(coveId => coveId < 1))
        {
            return TypedResults.BadRequest();
        }

        ArgumentNullException.ThrowIfNull(cards);

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(
                new LibraryStatusView([], LibraryStatusRefusalKind.NoInstanceConnected));
        }

        // Refused by the absence of a capability rather than by a probe: a generation registering no
        // role for this kind has nothing to ask, so nothing is sent.
        if (ReadingEntity(entityKind, target) is not { } reading)
        {
            return TypedResults.Ok(
                new LibraryStatusView(
                    [], LibraryStatusRefusalKind.WhisparrCannotAnswerForThisKind));
        }

        var rows = await cards
            .ReadEntityCardsAsync(reading, entityKind, target.Generation, request.CoveIds, ct)
            .ConfigureAwait(false);

        return TypedResults.Ok(new LibraryStatusView(rows, RefusalOver(rows)));
    }

    /// <summary>What the page as a whole could not be answered for, or that it could.</summary>
    /// <remarks>
    /// Unreachable is stated only where the instance was actually asked and answered nothing about
    /// any card. A page whose cards all carry no usable identifier claims nothing about the
    /// connection, because nothing left for it.
    /// </remarks>
    private static LibraryStatusRefusalKind RefusalOver(IReadOnlyList<LibraryStatusRow> rows)
    {
        var asked = rows.Count(row => row.Reading is not null);
        var unestablished = rows.Count(row => row.Reading is { Present: null });

        return asked > 0 && asked == unestablished
            ? LibraryStatusRefusalKind.InstanceUnreachable
            : LibraryStatusRefusalKind.None;
    }

    /// <summary>
    /// The entity kind the route segment names, or that it names none this route answers for.
    /// </summary>
    /// <remarks>
    /// A video is a bad request here rather than a refusal: it is a card kind this product expresses
    /// and no entity it monitors, so there is no instance answer to refuse on its behalf.
    /// </remarks>
    private static bool TryReadCardKind(string kind, out WhisparrEntityKind entityKind)
    {
        entityKind = default;

        if (!Enum.TryParse<LibraryCardKind>(kind, ignoreCase: true, out var card)
            || !Enum.IsDefined(card))
        {
            return false;
        }

        switch (card)
        {
            case LibraryCardKind.Studio:
                entityKind = WhisparrEntityKind.Studio;
                return true;
            case LibraryCardKind.Performer:
                entityKind = WhisparrEntityKind.Performer;
                return true;
            default:
                return false;
        }
    }
}
