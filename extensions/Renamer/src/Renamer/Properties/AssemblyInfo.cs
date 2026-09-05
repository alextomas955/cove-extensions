using System.Runtime.CompilerServices;

// The test project calls the internal shared batch seam (RunRenamerBatchAsync) and the internal
// TryParseKind mapping directly. Compile-time only: it adds no runtime or host assembly reference to
// the deployed Renamer.dll.
[assembly: InternalsVisibleTo("Renamer.Tests")]
