using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Providers;

/// <summary>The catalogue the stored provider choice names.</summary>
/// <remarks>
/// Asked for rather than injected, because the choice is a stored setting and reading it is I/O. A
/// call site awaits this once at the boundary of its own operation, under its own cancellation, and
/// uses the catalogue it answered for the rest of that operation.
/// </remarks>
internal delegate Task<IProviderCatalogue> ProviderCatalogueSource(CancellationToken ct);

// Registered per scope, so the stored choice is read once per request and both arms of one request
// answer against the same source. A host reconfigured between requests is picked up by the next one
// without the container being rebuilt.
internal sealed class ProviderCatalogueChoice(
    OptionsStore options, StashDbCatalogue stashDb, ThePornDbCatalogue thePornDb)
{
    // The catalogue is held rather than the task that read it, so a first caller that cancelled
    // leaves nothing behind for the next one to await.
    private IProviderCatalogue? _chosen;

    public async Task<IProviderCatalogue> ChooseAsync(CancellationToken ct)
    {
        if (_chosen is { } already)
        {
            return already;
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        IProviderCatalogue chosen =
            stored.SelectedGeneration == WhisparrGeneration.V2 ? thePornDb : stashDb;
        _chosen = chosen;
        return chosen;
    }
}
