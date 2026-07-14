using Cove.Core.Interfaces;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IScanService"/> for host-free unit tests of the ingest path — the ingest
/// analogue of <see cref="FakeCoveLibraryPort"/>. Records each <c>ImportDownloaded*</c> call as a
/// (kind, path, entityId) tuple and returns a monotonically-increasing fake Cove entity id, records
/// every <see cref="StartScan"/> call (the IMPT-05 fallback probe), and exposes a one-shot
/// "throw on the next import" hook so a test can simulate a missing/renamed-away path.
/// </summary>
internal sealed class FakeScanService : IScanService
{
    /// <summary>One recorded import: the kind method invoked, the path handed in, and the entity id (null = create).</summary>
    internal sealed record ImportCall(string Kind, string Path, int? EntityId);

    private readonly List<ImportCall> _imports = [];
    private readonly List<ScanOperationOptions> _scans = [];
    private int _nextId = 100;
    private Exception? _throwNext;

    /// <summary>Every recorded <c>ImportDownloaded*</c> call in invocation order.</summary>
    public IReadOnlyList<ImportCall> Imports => _imports;

    /// <summary>Every <see cref="StartScan"/> call's options in invocation order (the fallback probe).</summary>
    public IReadOnlyList<ScanOperationOptions> Scans => _scans;

    /// <summary>Arms a one-shot throw: the NEXT <c>ImportDownloaded*</c> call raises <paramref name="ex"/>, then the hook clears.</summary>
    public void ThrowOnNextImport(Exception ex) => _throwNext = ex;

    public string StartScan(ScanOperationOptions? options = null)
    {
        _scans.Add(options ?? new ScanOperationOptions());
        return "fake-scan-job";
    }

    public Task<int> ImportDownloadedVideoAsync(string path, int? videoId, CancellationToken ct = default)
        => Record("Video", path, videoId);

    public Task<int> ImportDownloadedImageAsync(string path, int? imageId, CancellationToken ct = default)
        => Record("Image", path, imageId);

    public Task<int> ImportDownloadedGalleryAsync(string path, int? galleryId, CancellationToken ct = default)
        => Record("Gallery", path, galleryId);

    public Task<int> ImportDownloadedAudioAsync(string path, int? audioId, CancellationToken ct = default)
        => Record("Audio", path, audioId);

    public Task<int> ImportDownloadedTextAsync(string path, int? textDocumentId, CancellationToken ct = default)
        => Record("Text", path, textDocumentId);

    private Task<int> Record(string kind, string path, int? id)
    {
        if (_throwNext is { } ex)
        {
            _throwNext = null; // one-shot: the arming clears so a subsequent fallback retry does not re-throw
            throw ex;
        }

        _imports.Add(new ImportCall(kind, path, id));
        return Task.FromResult(id ?? _nextId++);
    }
}
