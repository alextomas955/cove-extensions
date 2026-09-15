using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Addressing;

/// <summary>
/// Holds what each library root last agreed with on each generation's instance, so a run over many
/// folders costs one sample file and one set of probes per root.
/// </summary>
/// <remarks>
/// A singleton, and bounded by construction: one entry per generation and configured library root,
/// both of which an operator created by hand. Nothing per folder, per file or per entity joins it.
/// <para>
/// An entry carries the time it is good for, because a root that agreed on nothing is held for less
/// time than one that agreed.
/// </para>
/// <para>
/// The entry expires on its own rather than being invalidated by a writer. A root added or remounted
/// in Whisparr is a change this extension is never told about, so a reading with no expiry could stay
/// wrong until the host restarted.
/// </para>
/// </remarks>
internal sealed class FolderAgreementCache(TimeProvider clock)
{
    /// <summary>How long an agreed root is reused before it is established again.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long a root that agreed on nothing is reused.</summary>
    /// <remarks>
    /// Shorter than <see cref="Lifetime"/>: a refusal says only that asking is not currently worth
    /// doing, and a mount that came back has to be noticed within a run or two. It still outlives one
    /// run's own folder loop, so a run over thousands of folders cannot re-probe per folder.
    /// </remarks>
    internal static readonly TimeSpan RefusedLifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<
        (WhisparrGeneration Generation, string CoveRoot),
        (DateTimeOffset ReadAt, TimeSpan For, FolderAgreementReading Reading)> _entries = new();

    internal FolderAgreementReading? Held(WhisparrGeneration generation, string coveRoot)
        => _entries.TryGetValue((generation, coveRoot), out var entry)
            && clock.GetUtcNow() - entry.ReadAt < entry.For
                ? entry.Reading
                : null;

    internal void Hold(
        WhisparrGeneration generation, string coveRoot, FolderAgreementReading reading)
        => _entries[(generation, coveRoot)] = (
            clock.GetUtcNow(),
            reading.Refusal is null ? Lifetime : RefusedLifetime,
            reading);
}

/// <inheritdoc cref="IFolderAddressPort"/>
internal sealed class FolderAddressPort(
    ISampleFilePort samples,
    ICoveLibraryPort library,
    IReportedRootPort instanceRoots,
    OptionsStore options,
    FolderAgreementCache cache,
    ILogger log) : IFolderAddressPort
{
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

        var reading = cache.Held(target.Generation, coveRoot)
            ?? await EstablishAsync(target, coveRoot, ct).ConfigureAwait(false);

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

    private async Task<FolderAgreementReading> EstablishAsync(
        FolderAddressTarget target, string coveRoot, CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var reading = await ReadAgreementAsync(
            target,
            coveRoot,
            OutboundRefusalProjector.MappingFor(stored.OutboundMappings, coveRoot),
            ct).ConfigureAwait(false);

        // Held whether it agreed or not, so a root the instance cannot see is asked about at the
        // refusal rate rather than once per folder under it.
        cache.Hold(target.Generation, coveRoot, reading);
        return reading;
    }

    /// <summary>
    /// What <paramref name="coveRoot"/> agrees with, asking about <paramref name="mapping"/> where
    /// one is supplied and about the roots the instance declares where none is.
    /// </summary>
    /// <remarks>
    /// The roots the instance declares are not read at all under a supplied mapping. That read is an
    /// outbound request, and its answer has no part in a root an operator has settled.
    /// </remarks>
    private async Task<FolderAgreementReading> ReadAgreementAsync(
        FolderAddressTarget target, string coveRoot, string? mapping, CancellationToken ct)
    {
        var sample = await samples.ReadSampleFileAsync(coveRoot, ct).ConfigureAwait(false);
        if (sample is null)
        {
            return new FolderAgreementReading(
                null, FolderAgreementRefusal.NoFileToProbeWith, []);
        }

        var declared = string.IsNullOrWhiteSpace(mapping)
            ? await instanceRoots.ReadAsync(target.Generation, ct).ConfigureAwait(false)
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

    /// <summary>
    /// What the instance reports at <paramref name="candidate"/>, or null where it could not be read.
    /// </summary>
    /// <remarks>
    /// The candidate's own directory is what is asked for, and the answer is reduced to whether a file
    /// of the candidate's name is in it. A path the instance cannot open answers an empty listing
    /// rather than a failure, which is what makes the question a definitive yes or no.
    /// </remarks>
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
                target.BaseAddress, target.ApiKey, target.Generation, candidate[..separator], ct)
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
            WhisparrSyncLog.FolderProbeFailed(log, target.Generation, target.BaseAddress.Host);
            return null;
        }

        return FileIn(answer.Body, candidate);
    }

    /// <summary>
    /// What one listing says about <paramref name="candidate"/>, or null where it is not the
    /// instance's own listing shape.
    /// </summary>
    /// <remarks>
    /// An answer beyond the client's read bound, an unreadable body and an answer of another shape
    /// are one outcome: nothing was established either way. A body that IS a listing and names no
    /// such file is an answer, and reads as no file there.
    /// </remarks>
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

    /// <summary>The configured library root <paramref name="folder"/> sits under, or null.</summary>
    /// <remarks>
    /// The most specific of the roots that contain it. The tail is taken below this root, so a
    /// shallower one produces a tail carrying the very segments an instance root already holds, and
    /// the rebuilt candidate then names a path neither system has.
    /// </remarks>
    private string? RootContaining(string folder)
        => library.LibraryRoots
            .Where(root => PathCandidateGuard.TailBelow(folder, root) is not null)
            .OrderByDescending(root => PathCandidateGuard.Normalize(root).Length)
            .FirstOrDefault();
}
