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
            ILibraryCardIdentityPort sceneCards,
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
        if (!TryReadCardKind(kind, out var card)
            || request is not { CoveIds.Count: > 0 }
            || request.CoveIds.Count > MissingPerPage
            || request.CoveIds.Any(coveId => coveId < 1))
        {
            return TypedResults.BadRequest();
        }

        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(sceneCards);

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(
                new LibraryStatusView([], LibraryStatusRefusalKind.NoInstanceConnected));
        }

        var rows = card == LibraryCardKind.Video
            ? await ReadSceneCardsAsync(cards, sceneCards, target, request.CoveIds, ct)
                .ConfigureAwait(false)
            : await ReadEntityCardsAsync(cards, EntityKindOf(card), target, request.CoveIds, ct)
                .ConfigureAwait(false);

        return TypedResults.Ok(
            rows is null
                ? new LibraryStatusView([], LibraryStatusRefusalKind.WhisparrCannotAnswerForThisKind)
                : new LibraryStatusView(rows, RefusalOver(rows)));
    }

    /// <summary>
    /// One row per requested entity card, or null where the generation registers no role to ask.
    /// </summary>
    /// <remarks>
    /// Refused by the absence of a capability rather than by a probe: a generation registering no
    /// role for this kind has nothing to ask, so nothing is sent.
    /// </remarks>
    private static async Task<IReadOnlyList<LibraryStatusRow>?> ReadEntityCardsAsync(
        ILibraryStatusPort cards,
        WhisparrEntityKind entityKind,
        MonitoringTarget target,
        IReadOnlyList<int> coveIds,
        CancellationToken ct)
        => ReadingEntity(entityKind, target) is { } reading
            ? await cards
                .ReadEntityCardsAsync(reading, entityKind, target.Generation, coveIds, ct)
                .ConfigureAwait(false)
            : null;

    /// <summary>
    /// One row per requested scene card, or null where the generation reads no per-scene record.
    /// </summary>
    /// <remarks>
    /// The identity is resolved before anything leaves, so a card the library names no single
    /// identifier for costs no request and carries no reading. The requested order is the answer's,
    /// and a card the identity read answered nothing for carries a null reading in its place.
    /// </remarks>
    private static async Task<IReadOnlyList<LibraryStatusRow>?> ReadSceneCardsAsync(
        ILibraryStatusPort cards,
        ILibraryCardIdentityPort sceneCards,
        MonitoringTarget target,
        IReadOnlyList<int> coveIds,
        CancellationToken ct)
    {
        if (target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
                .Match<IWhisparrSceneStatusReading?>(reading => reading, _ => null)
            is not { } sceneStatus)
        {
            return null;
        }

        var identities = await sceneCards.ResolveAsync(coveIds, target.Generation, ct)
            .ConfigureAwait(false);

        var readings = await cards.ReadSceneCardsAsync(
                sceneStatus,
                target.Capabilities.Obtain<IWhisparrSceneExclusionReading>(),
                target.BaseAddress,
                target.ApiKey,
                identities,
                ct)
            .ConfigureAwait(false);

        return [.. coveIds.Select(coveId => new LibraryStatusRow(
            coveId, readings.TryGetValue(coveId, out var reading) ? reading : null))];
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
