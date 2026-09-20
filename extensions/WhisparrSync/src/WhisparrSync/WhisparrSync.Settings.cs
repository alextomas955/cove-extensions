using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapSettingsEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(HostConfigurationRoute,
            (ICurrentPrincipalAccessor principal) => HostConfiguration(principal))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapPost(ConnectionTestRoute,
            (ConnectionTestRequest request, ICurrentPrincipalAccessor principal,
             IConnectionTestRunner runner, CancellationToken ct)
                => ConnectionTestAsync(request, principal, runner, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(SettingsRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             CancellationToken ct)
                => ReadSettingsAsync(principal, options, credentials, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPut(SettingsRoute,
            (WhisparrSyncSettingsSaveRequest request, ICurrentPrincipalAccessor principal,
             OptionsStore options, OptionsWriteGate gate, ICredentialPort credentials,
             TimeProvider clock, CancellationToken ct)
                => SaveSettingsAsync(request, principal, options, gate, credentials, clock, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(ImportBannerRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, CancellationToken ct)
                => ReadImportBannerAsync(principal, options, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier as the settings routes, and for the same reason: both read and write stored
        // configuration, and the save aims this extension's stored credential at a third party.
        endpoints.MapGet(FolderMappingsRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, CancellationToken ct)
                => ReadFolderMappingsAsync(principal, options, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPut(FolderMappingsRoute,
            (FolderMappingSaveRequest request, ICurrentPrincipalAccessor principal,
             OptionsStore options, OptionsWriteGate gate, ICredentialPort credentials,
             IWhisparrClient client, ICoveLibraryPort library, IFolderAddressPort addressing,
             CancellationToken ct)
                => SaveFolderMappingAsync(
                    request, principal, options, gate, credentials, client, library, addressing, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    /// <summary>
    /// What this extension can see of the host's own configuration, of the host services it can
    /// obtain, and of its worker's lifecycle, from inside its container.
    /// </summary>
    /// <remarks>
    /// Opens no scope and touches no database: every member is a reading taken at load or an instant
    /// in this extension's own lifecycle, rather than library data.
    /// </remarks>
    internal Results<Ok<HostConfigurationView>, ForbiddenCode> HostConfiguration(
        ICurrentPrincipalAccessor principal)
        => HasReadPermission(principal)
            ? TypedResults.Ok(new HostConfigurationView(
                ConfigurationResolved,
                LibraryRootCount,
                WorkerStartedAtUtc,
                WorkerCancelledAtUtc,
                ScanServiceResolved,
                MetadataServerServiceResolved))
            : new ForbiddenCode();

    /// <summary>
    /// Tests one Whisparr connection, and reports which of the six outcomes it produced.
    /// </summary>
    /// <remarks>
    /// A request naming neither an address nor a key tests the STORED connection, which is the one
    /// call allowed to record what it read. A request naming either tests that pair and records
    /// nothing about a version, because the instance it reaches may not be the stored one.
    /// <para>
    /// The gate is checked BEFORE the body is read, so a principal without it causes no outbound
    /// request. Without that ordering the route would forward a request on behalf of a caller who is
    /// not allowed to configure this extension, and the classified answer would tell them what sits
    /// at an address they chose.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<ConnectionTestView>, ForbiddenCode>> ConnectionTestAsync(
        ConnectionTestRequest request,
        ICurrentPrincipalAccessor principal,
        IConnectionTestRunner runner,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runner);

        return TypedResults.Ok(
            string.IsNullOrWhiteSpace(request.Address) && string.IsNullOrWhiteSpace(request.ApiKey)
                ? await runner.TestStoredAsync(ct).ConfigureAwait(false)
                : await runner.TestTransientAsync(request.Address, request.ApiKey, ct).ConfigureAwait(false));
    }

    /// <summary>Reads the stored settings.</summary>
    /// <remarks>
    /// The answer cannot carry an API key: <see cref="WhisparrSyncSettingsView"/> has no member that
    /// could hold one, and the key is never read here — only its presence is.
    /// </remarks>
    internal static async Task<Results<Ok<WhisparrSyncSettingsView>, ForbiddenCode>> ReadSettingsAsync(
        ICurrentPrincipalAccessor principal,
        OptionsStore options,
        ICredentialPort credentials,
        CancellationToken ct)
        => HasConfigurePermission(principal)
            ? TypedResults.Ok(await ProjectSettingsAsync(options, credentials, ct).ConfigureAwait(false))
            : new ForbiddenCode();

    /// <summary>Applies one settings save and answers with the settings as they now stand.</summary>
    /// <remarks>
    /// The gate is checked before the body is read, so a principal without it writes nothing.
    /// <para>
    /// The key is written before the options blob. The two are separate stores with no transaction
    /// between them, so a save interrupted between the two leaves a stored key beside the address it
    /// was entered against rather than beside an address nothing was entered for.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<WhisparrSyncSettingsView>, ForbiddenCode>> SaveSettingsAsync(
        WhisparrSyncSettingsSaveRequest request,
        ICurrentPrincipalAccessor principal,
        OptionsStore options,
        OptionsWriteGate gate,
        ICredentialPort credentials,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        await credentials.ApplyAsync(
            WhisparrGeneration.V3, SettingsProjector.CredentialWriteFor(request.V3), now, ct)
            .ConfigureAwait(false);
        await credentials.ApplyAsync(
            WhisparrGeneration.V2, SettingsProjector.CredentialWriteFor(request.V2), now, ct)
            .ConfigureAwait(false);

        var persisted = await gate
            .MutateAsync(options, stored => SettingsProjector.Apply(stored, request), ct)
            .ConfigureAwait(false);

        // After both writes: the manifest reads this, and a value refreshed between them would name
        // a generation only one of the two stores had been given.
        _selectedGeneration = persisted.SelectedGeneration.ToString();

        return TypedResults.Ok(
            await ProjectSettingsAsync(persisted, credentials, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Reads the refusals outstanding, one line per Whisparr root that has any, and how many records
    /// the backstop could not take.
    /// </summary>
    /// <remarks>
    /// The configure tier, which is the tier Cove's own bulk extension-data route already requires to
    /// read these same values, so this route exposes nothing a caller could not already read. The gate
    /// is checked before the store, so a principal without it causes no read.
    /// <para>
    /// The answer holds recorded filesystem paths. Its size is the stored aggregate's, which the
    /// library's size does not enter into.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<ImportBannerView>, ForbiddenCode>> ReadImportBannerAsync(
        ICurrentPrincipalAccessor principal,
        OptionsStore options,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(options);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(ImportBannerView.From(stored.ImportRefusals, stored.ImportHealth));
    }

    /// <summary>
    /// Reads the Cove library roots the connected instance established no path for, one line each.
    /// </summary>
    /// <remarks>
    /// The configure tier, which is the tier Cove's own bulk extension-data route already requires to
    /// read these same stored values, so this route exposes nothing a caller could not already read.
    /// The gate is checked before the store, so a principal without it causes no read.
    /// <para>
    /// The answer holds recorded filesystem paths. Its size is the stored aggregate's, which the
    /// library's size does not enter into.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<FolderAgreementView>, ForbiddenCode>>
        ReadFolderMappingsAsync(
            ICurrentPrincipalAccessor principal, OptionsStore options, CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(options);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(
            FolderAgreementView.From(stored.OutboundRefusals, stored.OutboundMappings));
    }

    /// <summary>Stores where an operator says one library root is, once a probe has resolved it.</summary>
    /// <remarks>
    /// The probe is the authority. A mapping typed into this route is built into a candidate and put
    /// through the same reading a run takes, and it is stored only where the instance reported the
    /// library's own sample file at the size the library holds. A path taken on trust would attach the
    /// wrong file to a scene with nothing downstream to reveal it.
    /// <para>
    /// A save that resolved also clears that root's stored refusal, and the reading is held on the
    /// spot, so the next run neither reports a refusal that no longer holds nor waits out the previous
    /// reading's expiry.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<FolderMappingSaveResult>, ForbiddenCode>>
        SaveFolderMappingAsync(
            FolderMappingSaveRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            OptionsWriteGate gate,
            ICredentialPort credentials,
            IWhisparrClient client,
            ICoveLibraryPort library,
            IFolderAddressPort addressing,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(addressing);

        // The host's own spelling of the root, not the caller's: what is stored has to key the same
        // way the sample-file read and the folder loop key it.
        if (library.LibraryRoots.FirstOrDefault(root => SameRoot(root, request.CoveRoot))
            is not { } coveRoot)
        {
            return TypedResults.Ok(Answering(FolderMappingSaveOutcome.NotALibraryRoot));
        }

        if (string.IsNullOrWhiteSpace(request.InstancePath))
        {
            await StoreAsync(mapping: null, clearRefusal: false).ConfigureAwait(false);
            return TypedResults.Ok(Answering(FolderMappingSaveOutcome.Removed));
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(Answering(FolderMappingSaveOutcome.NotConfigured));
        }

        if (FilesystemReadingOn(target) is not { } role)
        {
            return TypedResults.Ok(
                Answering(
                    FolderMappingSaveOutcome.Refused,
                    FolderAgreementRefusal.InstanceCannotBeAsked));
        }

        var aimed = new FolderAddressTarget(
            target.Generation, target.BaseAddress, target.ApiKey, role);
        var addressed = await addressing
            .AddressAsync(aimed, coveRoot, request.InstancePath, ct).ConfigureAwait(false);

        if (addressed.InstancePath is not { } agreed)
        {
            return TypedResults.Ok(
                Answering(
                    FolderMappingSaveOutcome.Refused, addressed.Refusal, addressed.Tried));
        }

        // The spelling the probe verified rather than the one that was typed, so what is stored is
        // what the instance answered to.
        await StoreAsync(agreed, clearRefusal: true).ConfigureAwait(false);
        return TypedResults.Ok(
            Answering(FolderMappingSaveOutcome.Stored, refusal: null, addressed.Tried));

        Task<WhisparrSyncOptions> StoreAsync(string? mapping, bool clearRefusal)
            => gate.MutateAsync(
                options,
                stored => stored with
                {
                    OutboundMappings = OutboundRefusalProjector.WithMapping(
                        stored.OutboundMappings, coveRoot, mapping),
                    OutboundRefusals = clearRefusal
                        ? OutboundRefusalProjector.Fold(
                            stored.OutboundRefusals, refused: null, [coveRoot])
                        : stored.OutboundRefusals,
                },
                ct);

        static FolderMappingSaveResult Answering(
            FolderMappingSaveOutcome outcome,
            FolderAgreementRefusal? refusal = null,
            IReadOnlyList<string>? tried = null)
            => new(outcome, refusal, tried ?? []);
    }

    /// <summary>Whether two spellings name one configured library root.</summary>
    private static bool SameRoot(string? left, string? right)
        => string.Equals(
            ImportRootRefusals.NormaliseRoot(left),
            ImportRootRefusals.NormaliseRoot(right),
            StringComparison.Ordinal);

    private static async Task<WhisparrSyncSettingsView> ProjectSettingsAsync(
        OptionsStore options, ICredentialPort credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        return await ProjectSettingsAsync(
            await options.LoadAsync(ct).ConfigureAwait(false), credentials, ct).ConfigureAwait(false);
    }

    private static async Task<WhisparrSyncSettingsView> ProjectSettingsAsync(
        WhisparrSyncOptions stored, ICredentialPort credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        return SettingsProjector.ToView(
            stored,
            await credentials.HasKeyAsync(WhisparrGeneration.V3, ct).ConfigureAwait(false),
            await credentials.HasKeyAsync(WhisparrGeneration.V2, ct).ConfigureAwait(false));
    }
}
