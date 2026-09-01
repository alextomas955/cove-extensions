using System.Runtime.CompilerServices;

// The test projects call the internal shared batch seam (RunRenamerBatchAsync) and the internal
// TryParseKind mapping directly. This is a compile-time-only grant — it adds no runtime/host
// assembly reference to the deployed Renamer.dll. The attribute names exactly one assembly, so each
// test project that binds an internal member needs its own entry.
[assembly: InternalsVisibleTo("Renamer.Tests")]
[assembly: InternalsVisibleTo("Renamer.Cove.Tests")]
