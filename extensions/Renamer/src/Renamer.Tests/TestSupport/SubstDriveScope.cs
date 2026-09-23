namespace Renamer.Tests.TestSupport;

// The xUnit collection every test that maps a drive letter belongs to, so no two of them run at
// once. A drive letter is machine-global state, and one cross-volume case unmaps its own drive
// mid-test to make the reverse target offline. Run concurrently, another class mapping a free
// letter can take that one back before the assertion, and the path the test just took away resolves
// again - so the move reports a failure where the case is about a skip. SubstDrive's retry handles
// two classes racing for the same free letter, which is not this.
[CollectionDefinition(SubstDriveScope.CollectionName)]
public static class SubstDriveScope
{
    internal const string CollectionName = "subst drive letters";
}
