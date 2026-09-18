using System.Runtime.CompilerServices;

// The test project calls the internal shared batch seam and the internal kind parsing directly. This
// is compile-time only and adds no reference to the deployed Renamer.dll.
[assembly: InternalsVisibleTo("Renamer.Tests")]
