using System.Runtime.CompilerServices;
using Cove.Data;
using Cove.Plugins;

namespace Renamer.Tests.TestSupport;

// Registers this extension with CoveContext as a data extension for the whole test run, the way a
// host does, and lets one test add a second data extension for the span of a using block. The
// registration is process-wide and permanent, and both halves of that are deliberate. process-wide
// because CoveContext.SetDataExtensions is static and xUnit runs test classes in parallel:
// registration order across classes is not controllable, so anything that registers per class is a
// race. Doing it once, before any context exists, makes every context in the run agree - each
// EnsureCreatedAsync materializes the journal tables exactly as the host's own migration does, and
// every model resolves the journal entity types. permanent because deregistering would rebuild the
// model without those entity types while other classes are mid-test. There is no window in which
// this extension is absent; the only mutation any test may make is to add another registration and
// then drop back to this one.
internal static class CoveDataExtensionScope
{
    // The xUnit collection that serializes the suites which mutate the registration set.
    internal const string CollectionName = "Cove data extension registration";

    private static readonly global::Renamer.Renamer Registered = RenamerFixture.Create();

    [ModuleInitializer]
    internal static void RegisterForTheRun() => CoveContext.SetDataExtensions([Registered]);

    // Registers extra alongside this extension until the returned handle is disposed, then drops
    // back to this extension alone.
    internal static IDisposable WithAdditional(IDataExtension extra)
    {
        CoveContext.SetDataExtensions([Registered, extra]);
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => CoveContext.SetDataExtensions([Registered]);
    }
}

// Serializes the suites that mutate the process-wide data-extension registration.
[CollectionDefinition(CoveDataExtensionScope.CollectionName)]
public sealed class CoveDataExtensionRegistration
{
}
