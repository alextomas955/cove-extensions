using System.Text.Json;
using V3Api = Whisparr3.Net.Api;

namespace WhisparrSync.Whisparr;

// The scenes this instance has been told never to acquire. The list narrows by no parameter, so
// every read is the whole of it, consumed row by row and dropped.
internal sealed partial class WhisparrV3Instance
{
    private static readonly JsonSerializerOptions ExclusionRowShape = new(JsonSerializerDefaults.Web);

    // The two members of an exclusion row that are read, declared with no others so a row costs one
    // small object dropped before the next. The id is the row's own, which the removing route
    // addresses; the foreign id is the scene's.
    private sealed record ExclusionRow(int Id, string? ForeignId);

    public Task<WhisparrResponse> AddSceneExclusionAsync(string foreignId, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IImportListExclusionApi>().PostExclusionsAsync(
                V3BodyProjector.SceneExclusion(Named(foreignId)), ct));

    public Task<WhisparrResponse> RemoveSceneExclusionAsync(int exclusionId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusionId, 1);

        return GeneratedActAsync(
            api => api.Api<V3Api.IImportListExclusionApi>().DeleteExclusionsByIdAsync(exclusionId, ct));
    }

    // Each row is reduced to one question, so what this holds is the caller's set and never the
    // instance's. No row cap: one would stop part way and report the rest as not excluded, with
    // nothing saying so.
    public async Task<SceneExclusionReading> ReduceExclusionsAsync(
        IReadOnlyCollection<string> providerSceneIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerSceneIds);

        // Keyed without regard to case: an identifier is a hexadecimal uuid and each side stored its
        // own spelling. The caller's spelling is what is answered back.
        var asked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in providerSceneIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                asked[id] = id;
            }
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        if (asked.Count == 0)
        {
            return SceneExclusionReading.Naming(excluded);
        }

        // The rows named before a read stopped are accurate and the rest were never seen, so what
        // was gathered is answered under the statement that the list was not read whole. A caller
        // deciding a scene is not excluded needs the whole list to have arrived.
        var read = await OverExclusionRowsAsync(
            row =>
            {
                if (row.ForeignId is { Length: > 0 } named
                    && asked.TryGetValue(named, out var asAsked))
                {
                    excluded.Add(asAsked);
                }

                return true;
            },
            ct).ConfigureAwait(false);

        return read ? SceneExclusionReading.Naming(excluded) : SceneExclusionReading.DidNotComplete;
    }

    public async Task<SceneExclusionLookup> FindSceneExclusionAsync(
        string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        int? named = null;
        var read = await OverExclusionRowsAsync(
            row =>
            {
                // Compared without regard to case, as the reduce above keys. A non-positive
                // identifier is no address the removing route could take, so such a row is skipped.
                if (row.Id >= 1
                    && string.Equals(row.ForeignId, foreignId, StringComparison.OrdinalIgnoreCase))
                {
                    named = row.Id;
                    return false;
                }

                return true;
            },
            ct).ConfigureAwait(false);

        if (!read)
        {
            return SceneExclusionLookup.DidNotComplete;
        }

        return named is { } exclusionId
            ? SceneExclusionLookup.At(exclusionId)
            : SceneExclusionLookup.NamesNoExclusion;
    }

    // Reads the exclusion list row by row until visit answers false, and answers whether a whole
    // answer arrived and could be read.
    //
    // No parameter narrows this route: a filter key and a bare foreign id are both ignored and
    // answer the whole list under a success, and a foreign id as a further segment is a not-found.
    // The answer is read as it arrives and each row is dropped before the next, so nothing here
    // grows with what the instance holds. Sent once, since nothing is retained between rows.
    private async Task<bool> OverExclusionRowsAsync(Func<ExclusionRow, bool> visit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, WhisparrTransport.RequestUri(binding.BaseAddress, ExclusionsPath));
        request.Headers.Add(WhisparrTransport.ApiKeyHeader, binding.ApiKey);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(transport.AttemptBudget);

        try
        {
            using var response = await transport
                .OpenAsync(request, attempt.Token)
                .ConfigureAwait(false);

            if (!WhisparrTransport.IsSuccess((int)response.StatusCode))
            {
                return false;
            }

            var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var rows = JsonSerializer.DeserializeAsyncEnumerable<ExclusionRow>(
                    stream, ExclusionRowShape, attempt.Token);

                await foreach (var row in rows.ConfigureAwait(false))
                {
                    if (row is not null && !visit(row))
                    {
                        break;
                    }
                }
            }

            return true;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or JsonException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
