using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Import;

/// <summary>
/// Holds each generation's declared roots between reads, so a stream of deliveries costs one
/// outbound request rather than one per file.
/// </summary>
/// <remarks>
/// A singleton, and bounded by construction: one entry per generation, each a small list of paths an
/// operator created by hand. Nothing per file, per entity or per delivery joins it.
/// <para>
/// The entry expires on its own rather than being invalidated by a writer. A root added in Whisparr
/// is a change this extension is never told about, so a reading with no expiry would be a reading
/// that could stay wrong until the host restarted.
/// </para>
/// </remarks>
internal sealed class ReportedRootCache(TimeProvider clock)
{
    /// <summary>How long a reading is reused before the instance is asked again.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<WhisparrGeneration, (DateTimeOffset ReadAt, IReadOnlyList<string> Roots)>
        _entries = new();

    /// <summary>The held reading for <paramref name="generation"/>, or null when there is none in date.</summary>
    internal IReadOnlyList<string>? Held(WhisparrGeneration generation)
        => _entries.TryGetValue(generation, out var entry)
            && clock.GetUtcNow() - entry.ReadAt < Lifetime
                ? entry.Roots
                : null;

    /// <summary>Holds <paramref name="roots"/> as <paramref name="generation"/>'s current reading.</summary>
    internal void Hold(WhisparrGeneration generation, IReadOnlyList<string> roots)
        => _entries[generation] = (clock.GetUtcNow(), roots);
}

/// <inheritdoc cref="IReportedRootPort"/>
internal sealed class ReportedRootPort(
    IWhisparrClient client,
    OptionsStore options,
    ICredentialPort credentials,
    ReportedRootCache cache,
    ILogger log) : IReportedRootPort
{
    public async Task<IReadOnlyList<string>> ReadAsync(WhisparrGeneration generation, CancellationToken ct)
    {
        if (cache.Held(generation) is { } held)
        {
            return held;
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var apiKey = await credentials.ReadAsync(generation, ct).ConfigureAwait(false);

        // Refused here rather than by handing an empty pair to the client, so an unconfigured
        // connection reaches nothing that could make a request.
        if (!ConnectionTester.TryReadConnection(
                stored.ConnectionFor(generation)?.Address, apiKey, out var baseAddress, out _))
        {
            return [];
        }

        WhisparrResponse response;
        try
        {
            response = await client.ReadRootFoldersAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A shutdown is not a reading about the instance, so it must not be held as one.
            throw;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            WhisparrSyncLog.ReportedRootReadFailed(log, generation, baseAddress.Host);
            return [];
        }

        var roots = RootsIn(response);

        // Held even when empty, so an instance that declares none is asked at the same rate as one
        // that declares several.
        cache.Hold(generation, roots);
        return roots;
    }

    /// <summary>
    /// The root paths one answer declares, taken on parsed shape rather than on status.
    /// </summary>
    /// <remarks>
    /// One generation publishes no contract, so what an answer IS gets established by parsing it. A
    /// body that is not an array of objects carrying a string path yields nothing, which the caller
    /// refuses on.
    /// </remarks>
    private static IReadOnlyList<string> RootsIn(WhisparrResponse response)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(response.Body);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        return parsed is not JsonArray declared
            ? []
            : [.. declared
                .OfType<JsonObject>()
                .Select(root => (root["path"] as JsonValue)?.GetValue<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)];
    }
}
