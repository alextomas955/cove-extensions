using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapLibraryStatusEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Read tier. The body names the cards, so the route reaches the caller's set and never the
        // library, and it composes no write.
        endpoints.MapPost(LibraryStatusRoute,
            (string kind, LibraryStatusRequest request, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             LibraryStatusPort cards, ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => ReadLibraryStatusAsync(
                    kind, request, principal, options, credentials, client, cards, sceneCards, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);
    }

    // One row per requested identifier, in the order requested. The row count is the caller's and
    // never the library's.
    internal static async Task<Results<Ok<LibraryStatusView>, BadRequest, ForbiddenCode>>
        ReadLibraryStatusAsync(
            string kind,
            LibraryStatusRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            LibraryStatusPort cards,
            ILibraryCardIdentityPort sceneCards,
            CancellationToken ct)
    {
        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadCardKind(kind, out var card)
            || request is not { CoveIds.Count: > 0 }
            || request.CoveIds.Any(coveId => coveId < 1))
        {
            return TypedResults.BadRequest();
        }

        // Bounded to one rendered page, the same figure Cove's list pages default perPage to. A
        // longer body is answered as far as the page reaches, and the response says it was trimmed.
        var asked = request.CoveIds.Count > MissingPerPage
            ? request.CoveIds.Take(MissingPerPage).ToArray()
            : request.CoveIds;

        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(sceneCards);

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(
                new LibraryStatusView(card, [], LibraryStatusRefusalKind.NoInstanceConnected));
        }

        var answered = card == LibraryCardKind.Video
            ? await ReadSceneCardsAsync(cards, sceneCards, target, asked, ct)
                .ConfigureAwait(false)
            : await ReadEntityCardsAsync(cards, EntityKindOf(card), target, asked, ct)
                .ConfigureAwait(false);

        return TypedResults.Ok(
            answered.Rows is not { } rows
                ? new LibraryStatusView(
                    card, [], LibraryStatusRefusalKind.WhisparrCannotAnswerForThisKind)
                : new LibraryStatusView(
                    card,
                    rows,
                    RefusalOver(rows, answered.AnyReadDropped),
                    asked.Count != request.CoveIds.Count));
    }

    // Null rows where the generation registers no role for this kind: nothing is sent, so no read
    // can have dropped.
    private static async Task<(IReadOnlyList<LibraryStatusRow>? Rows, bool AnyReadDropped)>
        ReadEntityCardsAsync(
            LibraryStatusPort cards,
            WhisparrEntityKind entityKind,
            MonitoringTarget target,
            IReadOnlyList<int> coveIds,
            CancellationToken ct)
        => ReadingEntity(entityKind, target) is { } reading
            ? await cards
                .ReadEntityCardsAsync(
                    reading, entityKind, target.Generation, target.BaseAddress, coveIds, ct)
                .ConfigureAwait(false)
            : (null, false);

    // Null rows where the generation reads no per-scene record. Rows come back in the requested
    // order, and a card the identity read answered nothing for carries a null reading.
    private static async Task<(IReadOnlyList<LibraryStatusRow>? Rows, bool AnyReadDropped)>
        ReadSceneCardsAsync(
            LibraryStatusPort cards,
            ILibraryCardIdentityPort sceneCards,
            MonitoringTarget target,
            IReadOnlyList<int> coveIds,
            CancellationToken ct)
    {
        if (target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
                .Match<IWhisparrSceneStatusReading?>(reading => reading, _ => null)
            is not { } sceneStatus)
        {
            return (null, false);
        }

        var identities = await sceneCards.ResolveAsync(coveIds, target.Generation, ct)
            .ConfigureAwait(false);

        var answered = await cards.ReadSceneCardsAsync(
                sceneStatus,
                target.Capabilities.Obtain<IWhisparrSceneExclusionReading>(),
                target.BaseAddress,
                target.ApiKey,
                target.Generation,
                identities,
                ct)
            .ConfigureAwait(false);

        return (
            [.. coveIds.Select(coveId => new LibraryStatusRow(
                coveId,
                answered.Readings.TryGetValue(coveId, out var reading) ? reading : null))],
            answered.AnyReadDropped);
    }

    // A page-wide refusal needs both halves: a read left for the instance and did not come back, and
    // no card on the page established anything. An instance that answered establishes no page-wide
    // reason, whatever it answered, because an unresolved or unreadable identifier is a fact about
    // one card and that card already shows it by drawing no badge.
    private static LibraryStatusRefusalKind RefusalOver(
        IReadOnlyList<LibraryStatusRow> rows, bool anyReadDropped)
    {
        var asked = rows.Count(row => row.Reading is not null);
        var unestablished = rows.Count(row => row.Reading is { Present: null });

        return anyReadDropped && asked == unestablished
            ? LibraryStatusRefusalKind.InstanceUnreachable
            : LibraryStatusRefusalKind.None;
    }

    // A parse that succeeds is not the same as a member: the enum's underlying type accepts an
    // integer that names no member, so IsDefined is checked too.
    private static bool TryReadCardKind(string kind, out LibraryCardKind card)
        => Enum.TryParse(kind, ignoreCase: true, out card) && Enum.IsDefined(card);

    private static WhisparrEntityKind EntityKindOf(LibraryCardKind card)
        => card switch
        {
            LibraryCardKind.Studio => WhisparrEntityKind.Studio,
            LibraryCardKind.Performer => WhisparrEntityKind.Performer,
            _ => throw new ArgumentOutOfRangeException(
                nameof(card), card, "This card kind names no entity this product monitors."),
        };
}
