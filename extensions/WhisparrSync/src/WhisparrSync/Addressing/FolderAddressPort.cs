using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Addressing;

// A singleton, bounded by construction: one entry per generation and configured library root, both
// operator-created. Nothing per folder, file or entity joins it.
//
// An entry is good only for what it was established from: the instance it was read off and the
// stored path, if any, it was read under. A change to either misses the entry instead of waiting
// out its expiry, so a withdrawn or changed path is out of use on the next run. Everything else
// the reading depends on expires rather than being noticed, because a root added or remounted in
// Whisparr is a change this extension is never told about.
internal sealed class FolderAgreementCache(TimeProvider clock)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    // Shorter than Lifetime: a refusal says only that asking is not currently worth doing, and a
    // mount that came back has to be noticed within a run or two. It still outlives one run's
    // folder loop, so a run over thousands of folders cannot re-probe per folder.
    internal static readonly TimeSpan RefusedLifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<
        (WhisparrGeneration Generation, string Instance, string CoveRoot),
        (DateTimeOffset ReadAt, TimeSpan For, string? Stated, FolderAgreementReading Reading)>
        _entries = new();

    internal FolderAgreementReading? Held(
        FolderAddressTarget target, string coveRoot, string? stated)
        => _entries.TryGetValue(KeyFor(target, coveRoot), out var entry)
            && clock.GetUtcNow() - entry.ReadAt < entry.For
            && string.Equals(entry.Stated, stated, StringComparison.Ordinal)
                ? entry.Reading
                : null;

    internal void Hold(
        FolderAddressTarget target,
        string coveRoot,
        string? stated,
        FolderAgreementReading reading)
        => _entries[KeyFor(target, coveRoot)] = (
            clock.GetUtcNow(),
            reading.Refusal is null ? Lifetime : RefusedLifetime,
            stated,
            reading);

    // Keyed on the path, not the authority: TryReadAddress keeps the stored address's URL base, so
    // two instances behind one reverse proxy differ by that base alone. The trailing separator is
    // trimmed to agree with NormaliseAddress, which decides what a connection save calls the same
    // instance.
    private static (WhisparrGeneration, string, string) KeyFor(
        FolderAddressTarget target, string coveRoot)
        => (
            target.Binding.Generation,
            target.Binding.BaseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/'),
            coveRoot);
}

internal sealed class FolderAddressPort(
    ISampleFilePort samples,
    ICoveLibraryPort library,
    IReportedRootPort instanceRoots,
    OptionsStore options,
    FolderAgreementCache cache,
    ILogger log) : IFolderAddressPort
{
    private WhisparrSyncOptions? _stored;

    public async Task<AddressedFolder> AddressAsync(
        FolderAddressTarget target, string folder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (RootContaining(folder) is not { } coveRoot)
        {
            return new AddressedFolder(
                null, FolderAgreementRefusal.FolderUnderNoLibraryRoot, string.Empty, []);
        }

        // Read before the cache is asked, not inside the establishing path: a held reading taken
        // under a path that has since been withdrawn or changed must lose to the store.
        var stated = OutboundRefusalProjector.MappingFor(
            (await StoredAsync(ct).ConfigureAwait(false)).OutboundMappings, coveRoot);

        var reading = cache.Held(target, coveRoot, stated)
            ?? await EstablishAsync(target, coveRoot, stated, ct).ConfigureAwait(false);

        if (reading.InstanceRoot is not { } agreed)
        {
            return new AddressedFolder(null, reading.Refusal, coveRoot, reading.Tried);
        }

        var addressed = FolderAgreement.Address(folder, coveRoot, agreed);

        return addressed is null
            ? new AddressedFolder(
                null, FolderAgreementRefusal.FolderUnderNoLibraryRoot, coveRoot, reading.Tried)
            : new AddressedFolder(addressed, null, coveRoot, reading.Tried);
    }

    public async Task<AddressedFolder> AddressAsync(
        FolderAddressTarget target, string coveRoot, string supplied, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(coveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(supplied);

        var reading = await ReadAgreementAsync(target, coveRoot, supplied, ct).ConfigureAwait(false);

        if (reading.InstanceRoot is not null)
        {
            // Held under the spelling the probe answered to, not the one that was typed: that is
            // what the caller stores and what the next run reads.
            cache.Hold(target, coveRoot, reading.InstanceRoot, reading);
        }

        return new AddressedFolder(
            reading.InstanceRoot, reading.Refusal, coveRoot, reading.Tried);
    }

    public async Task<AddressedFolder> AgreedRootAsync(
        FolderAddressTarget target, string coveRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(coveRoot);

        // Read before the cache is asked, for the reason the folder overload states.
        var stated = OutboundRefusalProjector.MappingFor(
            (await StoredAsync(ct).ConfigureAwait(false)).OutboundMappings, coveRoot);

        var reading = cache.Held(target, coveRoot, stated)
            ?? await EstablishAsync(target, coveRoot, stated, ct).ConfigureAwait(false);

        return new AddressedFolder(reading.InstanceRoot, reading.Refusal, coveRoot, reading.Tried);
    }

    private async Task<FolderAgreementReading> EstablishAsync(
        FolderAddressTarget target, string coveRoot, string? stated, CancellationToken ct)
    {
        var reading = await ReadAgreementAsync(target, coveRoot, stated, ct).ConfigureAwait(false);

        // Held whether it agreed or not, so a root the instance cannot see is asked about at the
        // refusal rate rather than once per folder under it.
        cache.Hold(target, coveRoot, stated, reading);
        return reading;
    }

    // A load reads and deserialises the host store's blob every time. The port is scoped and a
    // run's folder loop is one sequential pass through one instance, so memoising costs one read
    // per run rather than one per folder.
    private async Task<WhisparrSyncOptions> StoredAsync(CancellationToken ct)
        => _stored ??= await options.LoadAsync(ct).ConfigureAwait(false);

    // Under a supplied mapping the instance's declared roots are not read: that outbound request
    // has no part in a root an operator has settled.
    private async Task<FolderAgreementReading> ReadAgreementAsync(
        FolderAddressTarget target, string coveRoot, string? mapping, CancellationToken ct)
    {
        var sample = await samples.ReadSampleFileAsync(coveRoot, ct).ConfigureAwait(false);
        if (sample is null)
        {
            return new FolderAgreementReading(
                null, FolderAgreementRefusal.NoFileToProbeWith, []);
        }

        // A root list that could not be read and an instance declaring none reach the same refusal
        // here, which names the instance's own list either way.
        var declared = string.IsNullOrWhiteSpace(mapping)
            ? await instanceRoots.ReadAsync(target.Binding.Generation, ct).ConfigureAwait(false) ?? []
            : [];
        var candidates = FolderAgreement.CandidatesFor(sample.Path, coveRoot, declared, mapping);
        if (candidates.Refusal is { } refused)
        {
            return new FolderAgreementReading(null, refused, candidates.Candidates);
        }

        var probed = new List<ProbedCandidate>(candidates.Candidates.Count);
        foreach (var candidate in candidates.Candidates)
        {
            var reading = await ProbeAsync(target, candidate, ct).ConfigureAwait(false);
            if (reading is null)
            {
                return new FolderAgreementReading(
                    null, FolderAgreementRefusal.ProbeCouldNotBeRead, candidates.Candidates);
            }

            probed.Add(new ProbedCandidate(candidate, reading));
        }

        return FolderAgreement.Resolve(sample.Path, coveRoot, probed, sample.Size);
    }

    // Returns null where the instance could not be read. The instance answers an empty listing for
    // a path it cannot open rather than failing, which makes the question a definite yes or no.
    private async Task<ProbedPath?> ProbeAsync(
        FolderAddressTarget target, string candidate, CancellationToken ct)
    {
        var separator = candidate.LastIndexOf('/');
        if (separator < 1)
        {
            return null;
        }

        WhisparrResponse answer;
        try
        {
            answer = await target.Filesystem.ReadInstanceFolderAsync(
                target.Binding.BaseAddress, target.Binding.ApiKey, target.Binding.Generation, candidate[..separator], ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A shutdown is not a reading about the instance, so it must not be held as one.
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            WhisparrSyncLog.FolderProbeFailed(log, target.Binding.Generation, target.Binding.BaseAddress.Host);
            return null;
        }

        return FileIn(answer.Body, candidate);
    }

    // Returns null where the body is not a listing: an answer past the client's read bound, an
    // unreadable body and another shape all establish nothing. A listing that names no such file
    // is an answer, and reads as no file there.
    private static ProbedPath? FileIn(string body, string candidate)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject listing || listing["files"] is not JsonArray files)
        {
            return null;
        }

        foreach (var file in files.OfType<JsonObject>())
        {
            if ((file["path"] as JsonValue)?.GetValue<string>() is not { } path
                || !string.Equals(
                    PathCandidateGuard.Normalize(path),
                    PathCandidateGuard.Normalize(candidate),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new ProbedPath(
                true,
                (file["size"] as JsonValue)?.TryGetValue<long>(out var size) == true ? size : null);
        }

        return new ProbedPath(false, null);
    }

    // The most specific containing root, or null. The tail is taken below this root, so a
    // shallower one gives a tail carrying segments an instance root already holds, and the rebuilt
    // candidate then names a path neither system has.
    private string? RootContaining(string folder)
        => library.LibraryRoots
            .Where(root => PathCandidateGuard.TailBelow(folder, root) is not null)
            .OrderByDescending(root => PathCandidateGuard.Normalize(root).Length)
            .FirstOrDefault();
}
