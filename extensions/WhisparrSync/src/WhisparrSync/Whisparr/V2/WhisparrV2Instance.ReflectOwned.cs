using System.Text.Json.Nodes;
using WhisparrSync.Monitoring;
using V2Api = Whisparr2.Net.Api;

namespace WhisparrSync.Whisparr;

// The reads a linking run takes before it acts, and the submit that attaches what it composed.
internal sealed partial class WhisparrV2Instance
{
    // Declared here because this generation serves all three of these routes, measured against a
    // running 2.2.0.231 instance: each answers as v3's does, and its hard-link document carried the
    // member this product reads.
    public Task<WhisparrResponse> ReadHardlinkSettingAsync(CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V2Api.IMediaManagementConfigApi>().GetMediaManagementConfigAsync(ct));

    public Task<WhisparrResponse> ReadNamingSettingsAsync(CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V2Api.INamingConfigApi>().GetNamingConfigAsync(ct));

    // The instance is asked to include what it already holds, so a file the library holds and the
    // instance has not attached is still answered for.
    public Task<WhisparrResponse> ListImportableFilesAsync(string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        // Under the library budget: the instance answers this only once it has walked and parsed
        // every entry in the directory, which on a few hundred files runs past the ordinary one.
        return GeneratedReadAsync(
            api => api.Api<V2Api.IManualImportApi>().ListManualImportAsync(
                folder: folder, filterExistingFiles: false, cancellationToken: ct),
            WhisparrTransport.LibraryReadTimeout);
    }

    public Task<WhisparrResponse> AttachOwnedFilesAsync(JsonNode files, CancellationToken ct)
    {
        var (verb, payload) = WhisparrTransport.VerbAndPayload(ReflectOwnedPlanner.Command(files));
        return GeneratedActAsync(
            api => api.Api<V2Api.CommandApi>().SendCommandAsync(verb, payload, ct));
    }

    public Task<WhisparrResponse> ReadInstanceFolderAsync(string directory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var asDirectory = WhisparrTransport.WithTrailingSeparator(directory);

        return GeneratedReadAsync(
            api => api.Api<V2Api.IFileSystemApi>().GetFileSystemAsync(
                path: asDirectory,
                includeFiles: true,
                allowFoldersWithoutTrailingSlashes: true,
                cancellationToken: ct));
    }
}
