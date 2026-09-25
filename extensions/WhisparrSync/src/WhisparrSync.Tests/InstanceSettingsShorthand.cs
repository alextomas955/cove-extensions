using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Tests;

// The folder answers live under one generation. A test that cares about one instance would otherwise
// carry the slot plumbing in every arrangement and every assertion.
internal static class InstanceSettingsShorthand
{
    // The selected generation, because that is the one every settings route reads.
    internal static WhisparrSyncGenerationInstanceSettings Instance(this WhisparrSyncOptions options)
        => options.InstanceSettingsOrEmptyFor(options.SelectedGeneration);

    internal static WhisparrSyncGenerationInstanceSettings Instance(
        this WhisparrSyncOptions options, WhisparrGeneration generation)
        => options.InstanceSettingsOrEmptyFor(generation);

    internal static WhisparrSyncOptions WithInstance(
        this WhisparrSyncOptions options,
        List<OutboundRootMapping>? outboundMappings = null,
        List<OutboundRootRefusal>? outboundRefusals = null,
        List<ImportRootRefusals>? importRefusals = null,
        bool rootsEstablished = true,
        WhisparrGeneration? generation = null)
    {
        var under = generation ?? options.SelectedGeneration;
        var held = options.InstanceSettingsOrEmptyFor(under);

        return options.WithInstanceSettingsFor(
            under,
            held with
            {
                OutboundMappings = outboundMappings ?? held.OutboundMappings,
                OutboundRefusals = outboundRefusals ?? held.OutboundRefusals,
                ImportRefusals = importRefusals ?? held.ImportRefusals,
                RootsEstablished = rootsEstablished,
            });
    }
}
