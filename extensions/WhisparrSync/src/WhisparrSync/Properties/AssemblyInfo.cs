using System.Runtime.CompilerServices;

// The test project unit-tests internal transport/result types (WhisparrClient, WhisparrResult,
// SystemStatus) directly, host-free — mirror Renamer's InternalsVisibleTo grant.
[assembly: InternalsVisibleTo("WhisparrSync.Tests")]
