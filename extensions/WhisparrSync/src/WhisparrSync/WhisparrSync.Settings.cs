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

        endpoints.MapGet(FolderMappingsRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, CancellationToken ct)
                => ReadFolderMappingsAsync(principal, options, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPut(FolderMappingsRoute,
            (FolderMappingSaveRequest request, ICurrentPrincipalAccessor principal,
             WhisparrAccess whisparr, OptionsWriteGate gate, ICoveLibraryPort library,
             IFolderAddressPort addressing, CancellationToken ct)
                => SaveFolderMappingAsync(
                    request, principal, whisparr, gate, library, addressing, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

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

    // A request naming neither an address nor a key tests the stored connection, which is the one
    // call allowed to record what it read: another pair may not reach the stored instance.
    // The gate is checked before the body is read, so a caller who cannot configure this extension
    // causes no outbound request and learns nothing about an address of their choosing.
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

    // The answer cannot carry an API key: the view has no member that could hold one, and only a
    // key's presence is read.
    internal static async Task<Results<Ok<WhisparrSyncSettingsView>, ForbiddenCode>> ReadSettingsAsync(
        ICurrentPrincipalAccessor principal,
        OptionsStore options,
        ICredentialPort credentials,
        CancellationToken ct)
        => HasConfigurePermission(principal)
            ? TypedResults.Ok(await ProjectSettingsAsync(options, credentials, ct).ConfigureAwait(false))
            : new ForbiddenCode();

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

        // The address is written with the key, in one row, before the options blob. An outbound
        // request is built from that row alone, so a reader running during this save sees one whole
        // pair or the other and never the new key beside the instance the old address named. The
        // blob that follows carries the same address for the page to read.
        var now = clock.GetUtcNow();
        var before = await options.LoadAsync(ct).ConfigureAwait(false);
        await credentials.ApplyAsync(
            WhisparrGeneration.V3,
            SettingsProjector.CredentialWriteFor(request.V3),
            SettingsProjector.AddressFor(request.V3, before.ConnectionFor(WhisparrGeneration.V3)),
            now,
            ct)
            .ConfigureAwait(false);
        await credentials.ApplyAsync(
            WhisparrGeneration.V2,
            SettingsProjector.CredentialWriteFor(request.V2),
            SettingsProjector.AddressFor(request.V2, before.ConnectionFor(WhisparrGeneration.V2)),
            now,
            ct)
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

    // The configure tier, which is what Cove's own bulk extension-data route already requires to
    // read these same stored values. The answer's size is the stored aggregate's, not the
    // library's.
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
        // The banner reports the instance in use. The other generation's refusals name roots this
        // one does not declare.
        var instance = stored.InstanceSettingsOrEmptyFor(stored.SelectedGeneration);
        return TypedResults.Ok(ImportBannerView.From(instance.ImportRefusals, stored.ImportHealth));
    }

    // The configure tier, for the reason above. The answer's size is the stored aggregate's, not
    // the library's.
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
        var instance = stored.InstanceSettingsOrEmptyFor(stored.SelectedGeneration);
        return TypedResults.Ok(
            FolderAgreementView.From(instance.OutboundRefusals, instance.OutboundMappings));
    }

    // A typed mapping is stored only where the probe found the library's own sample file at the
    // size the library holds. A path taken on trust would attach the wrong file to a scene with
    // nothing downstream to reveal it. A save that resolved also clears that root's stored
    // refusal, so the next run does not report a refusal that no longer holds.
    internal static async Task<Results<Ok<FolderMappingSaveResult>, ForbiddenCode>>
        SaveFolderMappingAsync(
            FolderMappingSaveRequest request,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            OptionsWriteGate gate,
            ICoveLibraryPort library,
            IFolderAddressPort addressing,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(whisparr);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(library);
        var options = whisparr.Options;
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

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
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

        var aimed = new FolderAddressTarget(target.Binding, role);
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
                stored => stored.WithInstanceSettingsFor(
                    stored.SelectedGeneration,
                    stored.InstanceSettingsOrEmptyFor(stored.SelectedGeneration) with
                    {
                        OutboundMappings = OutboundRefusalProjector.WithMapping(
                            stored.InstanceSettingsOrEmptyFor(stored.SelectedGeneration).OutboundMappings,
                            coveRoot,
                            mapping),
                        OutboundRefusals = clearRefusal
                            ? OutboundRefusalProjector.Fold(
                                stored.InstanceSettingsOrEmptyFor(stored.SelectedGeneration).OutboundRefusals,
                                refused: null,
                                [coveRoot])
                            : stored.InstanceSettingsOrEmptyFor(stored.SelectedGeneration).OutboundRefusals,
                    }),
                ct);

        static FolderMappingSaveResult Answering(
            FolderMappingSaveOutcome outcome,
            FolderAgreementRefusal? refusal = null,
            IReadOnlyList<string>? tried = null)
            => new(outcome, refusal, tried ?? []);
    }

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
