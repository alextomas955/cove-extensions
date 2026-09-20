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
        // The read tier. This route names its cards in the body, the way the bulk route names its
        // scenes, so its reach is the set the caller sent and never the library. It composes no
        // write, and a caller who may see the library may see a read-only status over it.
        endpoints.MapPost(LibraryStatusRoute,
            (string kind, LibraryStatusRequest request, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             LibraryStatusPort cards, ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => ReadLibraryStatusAsync(
                    kind, request, principal, options, credentials, client, cards, sceneCards, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);
    }

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
            LibraryStatusPort cards,
            ILibraryCardIdentityPort sceneCards,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
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

        // One rendered page, which is the same figure Cove's own list pages default their perPage
        // to. Referenced rather than restated, so the two cannot drift. A body over it is answered
        // as far as the page reaches and reports the remainder, so no caller has to hold this figure
        // to send a request this route can answer.
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

    /// <summary>
    /// One row per requested entity card, or null rows where the generation registers no role to ask.
    /// </summary>
    /// <remarks>
    /// Refused by the absence of a capability rather than by a probe: a generation registering no
    /// role for this kind has nothing to ask, so nothing is sent and no read can have dropped.
    /// </remarks>
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

    /// <summary>
    /// One row per requested scene card, or null rows where the generation reads no per-scene record.
    /// </summary>
    /// <remarks>
    /// The identity is resolved before anything leaves, so a card the library names no single
    /// identifier for costs no request and carries no reading. The requested order is the answer's,
    /// and a card the identity read answered nothing for carries a null reading in its place.
    /// </remarks>
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

    /// <summary>What the page as a whole could not be answered for, or that it could.</summary>
    /// <remarks>
    /// Both halves of the sentence have to hold before it is stated. A read has to have left for the
    /// instance and not come back, which is what "could not reach Whisparr" says, and no card on the
    /// page may have established anything, which is what "no card can show a status" says.
    /// <para>
    /// An instance that answered establishes no reason here whatever it answered. A stored identifier
    /// its own metadata source resolves to nothing, or to several entities, or an answer this cannot
    /// read, are each a fact about ONE card, and the card already carries it by drawing no badge.
    /// Naming the connection for them sends a reader to audit an instance that answered every request
    /// it was given, and one sentence for the page cannot truthfully describe one card out of forty.
    /// </para>
    /// <para>
    /// A page whose cards all carry no usable identifier claims nothing either, because nothing left
    /// for it.
    /// </para>
    /// </remarks>
    private static LibraryStatusRefusalKind RefusalOver(
        IReadOnlyList<LibraryStatusRow> rows, bool anyReadDropped)
    {
        var asked = rows.Count(row => row.Reading is not null);
        var unestablished = rows.Count(row => row.Reading is { Present: null });

        return anyReadDropped && asked == unestablished
            ? LibraryStatusRefusalKind.InstanceUnreachable
            : LibraryStatusRefusalKind.None;
    }

    /// <summary>The card kind the route segment names, or that it names none.</summary>
    /// <remarks>
    /// A parse that succeeds is not the same as a member: the enum's underlying type accepts an
    /// integer inside no member, and every arm downstream would then take its default.
    /// </remarks>
    private static bool TryReadCardKind(string kind, out LibraryCardKind card)
        => Enum.TryParse(kind, ignoreCase: true, out card) && Enum.IsDefined(card);

    /// <summary>The monitored-entity kind a card kind stands for.</summary>
    /// <remarks>
    /// A video reaches this for nothing: it is a card kind this product expresses and no entity it
    /// monitors, so its own branch answers it and every arm here would throw.
    /// </remarks>
    private static WhisparrEntityKind EntityKindOf(LibraryCardKind card)
        => card switch
        {
            LibraryCardKind.Studio => WhisparrEntityKind.Studio,
            LibraryCardKind.Performer => WhisparrEntityKind.Performer,
            _ => throw new ArgumentOutOfRangeException(
                nameof(card), card, "This card kind names no entity this product monitors."),
        };
}
