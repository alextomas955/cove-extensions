using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Import;

// Holds what each instance last said about its roots, so a stream of deliveries costs one outbound
// request rather than one per file. A singleton. An entry is keyed by the generation and the
// resolved address together, because an instance that moved answers about its own roots and not the
// previous one's.
//
// What it holds is one entry per instance contacted inside a lifetime, each a small list of
// hand-created paths, with nothing per file or per delivery joining it. Expired entries are dropped
// as a later one is written, so a reader editing the address repeatedly does not leave one entry
// behind per spelling tried.
//
// An entry expires on its own rather than being invalidated by a writer, because a root added in
// Whisparr is a change this extension is never told about.
internal sealed class ReportedRootCache(TimeProvider clock)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    // Shorter than Lifetime: this reading says only that asking is not currently worth doing, and
    // an instance that came back has to be noticed within a wake or two. It still outlives one
    // request's timeout, so a burst during an outage cannot re-probe per delivery.
    internal static readonly TimeSpan NothingToReadLifetime = TimeSpan.FromSeconds(30);

    // A resolution with no binding has no address to key on. Its own key, rather than one folded
    // onto an address, so configuring an address afterwards does not find a reading made about
    // nothing.
    private const string NoInstance = "";

    private readonly ConcurrentDictionary<
        (WhisparrGeneration Generation, string Address),
        (DateTimeOffset ReadAt, TimeSpan For, IReadOnlyList<string>? Roots)>
        _entries = new();

    internal static (WhisparrGeneration Generation, string Address) NothingConfigured(
        WhisparrGeneration generation) => (generation, NoInstance);

    // The roots are null on a held reading nothing could be established from, which is a different
    // fact from an instance declaring none.
    internal bool TryHeld(
        (WhisparrGeneration Generation, string Address) instance, out IReadOnlyList<string>? roots)
    {
        roots = null;
        if (_entries.TryGetValue(instance, out var entry)
            && clock.GetUtcNow() - entry.ReadAt < entry.For)
        {
            roots = entry.Roots;
            return true;
        }

        return false;
    }

    internal void Hold(
        (WhisparrGeneration Generation, string Address) instance, IReadOnlyList<string> roots)
        => Write(instance, Lifetime, roots);

    // One reading for both an unconfigured connection and a request that found nobody: the caller's
    // next step is the same.
    internal void HoldNothingToRead((WhisparrGeneration Generation, string Address) instance)
        => Write(instance, NothingToReadLifetime, null);

    private void Write(
        (WhisparrGeneration Generation, string Address) instance,
        TimeSpan For,
        IReadOnlyList<string>? roots)
    {
        var now = clock.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.ReadAt >= entry.For)
            {
                _entries.TryRemove(key, out _);
            }
        }

        _entries[instance] = (now, For, roots);
    }
}

internal sealed class ReportedRootPort(
    IWhisparrInstanceFactory instances,
    ICredentialPort credentials,
    ReportedRootCache cache,
    ILogger log) : IReportedRootPort
{
    public async Task<IReadOnlyList<string>?> ReadAsync(
        WhisparrGeneration generation, CancellationToken ct)
    {
        // Resolved before the cache is consulted, because an entry answers for the instance it was
        // read from. Resolving reads the credential row and makes no outbound request, so a stream
        // of deliveries still costs one call to the instance rather than one per file.
        var resolution = await OutboundPair
            .ResolveAsync(credentials, generation, ct).ConfigureAwait(false);
        if (resolution.Binding is not { } binding)
        {
            var nothingConfigured = ReportedRootCache.NothingConfigured(generation);
            if (cache.TryHeld(nothingConfigured, out var unconfigured))
            {
                return unconfigured;
            }

            cache.HoldNothingToRead(nothingConfigured);
            return null;
        }

        if (cache.TryHeld(binding.Instance, out var held))
        {
            return held;
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
            cache.HoldNothingToRead(binding.Instance);
            return null;
        }

        if (RootsIn(response) is not { } roots)
        {
            cache.HoldNothingToRead(binding.Instance);
            return null;
        }

        // Held even when empty, so an instance that declares none is asked at the same rate as one
        // that declares several.
        cache.Hold(binding.Instance, roots);
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
