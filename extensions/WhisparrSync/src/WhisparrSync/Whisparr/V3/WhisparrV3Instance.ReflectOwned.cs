using System.Text.Json.Nodes;
using WhisparrSync.Monitoring;
using V3Api = Whisparr3.Net.Api;

namespace WhisparrSync.Whisparr;

// The reads a linking run takes before it acts, and the submit that attaches what it composed.
internal sealed partial class WhisparrV3Instance
{
    public Task<WhisparrResponse> ReadHardlinkSettingAsync(CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IMediaManagementConfigApi>().GetConfigMediamanagementAsync(ct));

    public Task<WhisparrResponse> ReadNamingSettingsAsync(CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.INamingConfigApi>().GetConfigNamingAsync(ct));

    // The instance is asked to include what it already holds, so a file the library holds and the
    // instance has not attached is still answered for.
    //
    // Sent under the library budget rather than the ordinary one: the instance walks and parses
    // every entry in the folder before it answers, so a folder holding a few hundred files takes
    // longer than the ordinary timeout allows. Refused for time, the folder is counted as refused
    // and its files are silently left unlinked.
    public Task<WhisparrResponse> ListImportableFilesAsync(string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return GeneratedReadAsync(
            api => api.Api<V3Api.IManualImportApi>().GetManualimportAsync(
                folder: folder, filterExistingFiles: false, cancellationToken: ct),
            WhisparrTransport.LibraryReadTimeout);
    }

    // The instance's own reading of the file itself. Sent under the library budget: it opens the
    // file and reads it, which on a large one runs past the ordinary budget.
    public Task<WhisparrResponse> ReadFileAsync(OwnedFilePlacement file, CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IManualImportApi>().PostManualimportAsync(
                V3BodyProjector.ReadOwnedFile(file), ct),
            WhisparrTransport.LibraryReadTimeout);

    public Task<WhisparrResponse> AttachOwnedFilesAsync(JsonNode files, CancellationToken ct)
        => GeneratedCommandAsync(ReflectOwnedPlanner.Command(files), ct);

    public Task<WhisparrResponse> ReadInstanceFolderAsync(string directory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var asDirectory = WhisparrTransport.WithTrailingSeparator(directory);

        return GeneratedReadAsync(
            api => api.Api<V3Api.IFileSystemApi>().GetFilesystemAsync(
                path: asDirectory,
                includeFiles: true,
                allowFoldersWithoutTrailingSlashes: true,
                cancellationToken: ct));
    }
}
