using System.Runtime.CompilerServices;

// The test project calls the internal shared batch seam (RunRenamerBatchAsync) and the internal
// TryParseKind mapping directly. This is a compile-time-only grant — it adds no runtime/host
// assembly reference to the deployed Renamer.dll.
[assembly: InternalsVisibleTo("Renamer.Tests")]
