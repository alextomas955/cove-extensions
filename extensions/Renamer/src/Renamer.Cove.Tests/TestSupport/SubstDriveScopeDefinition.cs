namespace Renamer.Tests.TestSupport;

/// <summary>
/// The drive-letter collection, declared for this assembly so the tests here that map a drive letter
/// do not run at once.
/// </summary>
/// <remarks>
/// Serialization comes from the <c>[Collection]</c> attributes themselves; this declaration carries no
/// fixture and adds none. It exists so the collection has a named declaration in the assembly whose
/// tests join it, taking its name from <see cref="SubstDriveScope.CollectionName"/> so the two
/// assemblies cannot come to name different collections. The constraint they both hold is written on
/// <see cref="SubstDriveScope"/>.
///
/// Tests in the two assemblies still run concurrently with each other.
/// </remarks>
[CollectionDefinition(SubstDriveScope.CollectionName)]
public static class SubstDriveScopeDefinition;
