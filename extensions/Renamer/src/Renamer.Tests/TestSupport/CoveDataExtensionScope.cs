using System.Runtime.CompilerServices;
using Cove.Data;

namespace Renamer.Tests.TestSupport;

// Registers this extension with CoveContext as a data extension once, before any context is built,
// the way the host does at startup. The registration is static and process-wide, so every context in
// the run materializes the journal tables and resolves the journal entity types.
internal static class CoveDataExtensionScope
{
    [ModuleInitializer]
    internal static void RegisterForTheRun() => CoveContext.SetDataExtensions([RenamerFixture.Create()]);
}
