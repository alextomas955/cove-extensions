namespace Renamer.Tests.TestSupport;

/// <summary>
/// The xUnit collection every test that maps a drive letter belongs to, so no two of them run at once.
/// </summary>
/// <remarks>
/// A drive letter is machine-global state, and one cross-volume case unmaps its own drive
/// mid-test to make the reverse target offline. Run concurrently, another class mapping a free letter
/// can take that one back before the assertion, and the path the test just took away resolves again -
/// so the move reports a failure where the case is about a skip. <see cref="SubstDrive"/>'s retry
/// handles two classes racing for the same FREE letter, which is not this.
/// </remarks>
[CollectionDefinition(SubstDriveScope.CollectionName)]
public sealed class SubstDriveScope
{
    internal const string CollectionName = "subst drive letters";
}
