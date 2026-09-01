namespace Renamer.Tests.TestSupport;

/// <summary>
/// The drive-letter collection, declared for this assembly so the tests here that map a drive letter
/// do not run at once.
/// </summary>
/// <remarks>
/// xUnit scopes a collection to a single assembly, so a definition in the referenced pure assembly
/// does not serialize anything here, and its absence is silent: xUnit1041 reports only a constructor
/// needing fixture data with no source, and this definition carries no fixture. The name comes from
/// <see cref="SubstDriveScope.CollectionName"/> so the two declarations cannot name different
/// collections. The constraint they both hold is written on <see cref="SubstDriveScope"/>.
///
/// Tests in the two assemblies still run concurrently with each other.
/// </remarks>
[CollectionDefinition(SubstDriveScope.CollectionName)]
public static class SubstDriveScopeDefinition;
