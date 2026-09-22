using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Import;

// Holds what each generation's instance last said about its roots, so a stream of deliveries costs
// one outbound request rather than one per file. A singleton, bounded by construction: one entry
// per generation, each a small list of hand-created paths. Nothing per file or per delivery joins
// it.
// An entry expires on its own rather than being invalidated by a writer, because a root added in
// Whisparr is a change this extension is never told about.
internal sealed class ReportedRootCache(TimeProvider clock)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    // Shorter than Lifetime: this reading says only that asking is not currently worth doing, and
    // an instance that came back has to be noticed within a wake or two. It still outlives one
    // request's timeout, so a burst during an outage cannot re-probe per delivery.
    internal static readonly TimeSpan NothingToReadLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<
        WhisparrGeneration, (DateTimeOffset ReadAt, TimeSpan For, IReadOnlyList<string>? Roots)>
        _entries = new();

    // The roots are null on a held reading nothing could be established from, which is a different
    // fact from an instance declaring none.
    internal bool TryHeld(WhisparrGeneration generation, out IReadOnlyList<string>? roots)
    {
        roots = null;
        if (_entries.TryGetValue(generation, out var entry)
            && clock.GetUtcNow() - entry.ReadAt < entry.For)
        {
            roots = entry.Roots;
            return true;
        }

        return false;
    }

    internal void Hold(WhisparrGeneration generation, IReadOnlyList<string> roots)
        => _entries[generation] = (clock.GetUtcNow(), Lifetime, roots);

    // One reading for both an unconfigured connection and a request that found nobody: the caller's
    // next step is the same.
    internal void HoldNothingToRead(WhisparrGeneration generation)
        => _entries[generation] = (clock.GetUtcNow(), NothingToReadLifetime, null);
}

internal sealed class ReportedRootPort(
    IWhisparrInstanceFactory instances,
    OptionsStore options,
    ICredentialPort credentials,
    ReportedRootCache cache,
    ILogger log) : IReportedRootPort
{
    public async Task<IReadOnlyList<string>?> ReadAsync(
        WhisparrGeneration generation, CancellationToken ct)
    {
        if (cache.TryHeld(generation, out var held))
        {
            return held;
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);

        // Resolved for the generation this port was asked about, which a delivery names and the
        // settings do not. Refused where nothing is configured, so an unconfigured connection
        // reaches nothing that could make a request.
        var resolution = await OutboundPair
            .ResolveAsync(stored, credentials, generation, ct).ConfigureAwait(false);
        if (resolution.Binding is not { } binding)
        {
            cache.HoldNothingToRead(generation);
            return null;
        }

        WhisparrResponse response;
        try
        {
            response = await instances
                .Bound(binding)
                .ReadRootFoldersAsync(ct)
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
            WhisparrSyncLog.ReportedRootReadFailed(log, generation, binding.BaseAddress.Host);

            // Held, so a burst of deliveries during an outage does not re-pay the client's timeout
            // and retry once per file inside the inbound request pipeline.
            cache.HoldNothingToRead(generation);
            return null;
        }

        if (RootsIn(response) is not { } roots)
        {
            cache.HoldNothingToRead(generation);
            return null;
        }

        // Held even when empty, so an instance that declares none is asked at the same rate as one
        // that declares several.
        cache.Hold(generation, roots);
        return roots;
    }

    // Null where the answer is not a list of roots. One generation publishes no contract, so the
    // shape comes from parsing, and a body that is not an array establishes nothing about what the
    // instance declares, which is not the same as declaring none.
    private static IReadOnlyList<string>? RootsIn(WhisparrResponse response)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(response.Body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        return parsed is not JsonArray declared
            ? null
            : [.. declared
                .OfType<JsonObject>()
                .Select(root => (root["path"] as JsonValue)?.GetValue<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)];
    }
}
