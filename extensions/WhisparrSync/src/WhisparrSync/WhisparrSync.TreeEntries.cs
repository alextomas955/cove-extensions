using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Linking;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    // Null where the connected generation names a scene by a row of its own: it needs no site to
    // find one, so its entries are addressed without any of the resolution below.
    private Func<WhisparrEntityKind, int, EntityTreeFolder, CancellationToken,
        IAsyncEnumerable<IReadOnlyDictionary<string, EntryAddress>>>? SupplyingEntries(
            MonitoringTarget target, IServiceProvider services)
    {
        if (target.Reads is not IWhisparrSiteSceneReading rows
            || target.Reads is not IWhisparrStudioActing studios)
        {
            return null;
        }

        var generation = target.Binding.Generation;
        var scenes = services.GetRequiredService<IEntitySceneIdentityPort>();
        var library = services.GetRequiredService<ILibrarySceneIdentityPort>();
        var links = services.GetRequiredService<ITreeLinkPort>();
        var catalogues = services.GetRequiredService<ProviderCatalogueSource>();

        return AddressingAsync;

        async IAsyncEnumerable<IReadOnlyDictionary<string, EntryAddress>> AddressingAsync(
            WhisparrEntityKind kind,
            int coveId,
            EntityTreeFolder inTree,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var held = await ContainedAsync(
                () => studios.ReadStudioAsync(inTree.RemoteId, ct), target, _log, ct)
                .ConfigureAwait(false);

            // Without the site's own row nothing in the folder can be addressed. Handing over a
            // chunk that addresses nothing leaves every file where it is, which is what a folder of
            // links the instance parses nothing out of needs.
            if (held is null || MonitoringProjector.EntityIdIn(held.Body) is not { } siteRow)
            {
                WhisparrSyncLog.TreeEntriesUnaddressable(_log, inTree.RemoteId);
                yield break;
            }

            var ports = new TreeEntryPorts(
                sceneCt => scenes.SceneIdentitiesFor(kind, coveId, generation, sceneCt),
                async (identity, numberCt) =>
                    await (await catalogues(numberCt).ConfigureAwait(false))
                        .ResolveNumericSceneIdAsync(identity, numberCt)
                        .ConfigureAwait(false),
                (numbers, rowCt) => rows.ReduceSiteSceneRowsAsync(siteRow, numbers, rowCt),
                (identity, nameCt) => LinkNamesOfAsync(
                    library, links, generation, inTree, identity, nameCt));

            // Counters, never the chunks themselves: the line names how many scenes the pass
            // reached, and an entity reaches the size of the library.
            var tally = TreeEntryTally.Nothing;
            await foreach (var batch in TreeEntryPass.AddressedAsync(ports, siteRow, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                tally = tally.Plus(batch.Tally);
                yield return batch.ByName;
            }

            WhisparrSyncLog.TreeEntriesAddressed(
                _log, inTree.RemoteId, tally.Addressed, tally.Unnumbered, tally.Unresolved);
        }
    }

    // The name a scene's file carries in the entity's folder, computed from the file rather than
    // read out of a listing: the name is the file's identity, so one file answers one name and the
    // folder is never searched for it.
    private static async IAsyncEnumerable<string> LinkNamesOfAsync(
        ILibrarySceneIdentityPort library,
        ITreeLinkPort links,
        WhisparrGeneration generation,
        EntityTreeFolder inTree,
        string identity,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var path in library
            .SceneFilePathsUnder(identity, generation, inTree.CoveRoot, ct)
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            if (links.Identify(path) is not { } probed
                || TreePathGuard.LinkPathIn(inTree.Folder, probed.Identity, path) is not { } link)
            {
                continue;
            }

            yield return Path.GetFileName(link);
        }
    }
}
