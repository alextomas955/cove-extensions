using System.Runtime.CompilerServices;

// The test project drives the outbound client and the connection tester directly, and reads the
// classifier's media-type rule. Both types stay internal: they are how this extension is built, not
// what it offers. This is a compile-time-only grant — it adds no runtime or host assembly reference
// to the deployed WhisparrSync.dll.
[assembly: InternalsVisibleTo("WhisparrSync.Tests")]
