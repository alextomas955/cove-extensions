using System.Runtime.CompilerServices;

// The Cove-dependent test project consumes the internal TestSupport/ helpers (RenamerFixture, Dest,
// LibraryPathsFixture) that live here. InternalsVisibleTo names exactly one assembly per attribute.
[assembly: InternalsVisibleTo("Renamer.Cove.Tests")]
