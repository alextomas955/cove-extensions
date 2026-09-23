using Cove.Core.Interfaces;
using Renamer.Execution;

namespace Renamer.Tests.Execution;

public sealed class LibraryRootsTests
{
    private static CoveConfiguration ConfigWith(params string[] paths) =>
        new() { CovePaths = [.. paths.Select(p => new CovePath { Path = p })] };

    [Theory]
    // A separator style and a trailing separator are the two differences one folder can be written
    // with here, and each must collapse to the same single spelling.
    [InlineData(@"C:\Videos", "C:/Videos")]
    [InlineData("C:/Videos/", "C:/Videos")]
    [InlineData(@"C:\Videos\", "C:/Videos")]
    [InlineData(@"C:\Videos\Shows\", "C:/Videos/Shows")]
    [InlineData("/data", "/data")]
    [InlineData("/data/", "/data")]
    public void APathIsSpelledOneWay_WhateverSeparatorsItArrivesWith(string given, string expected) =>
        Assert.Equal([expected], CoveRenamerDataPort.ReadLibraryRoots(ConfigWith(given)));

    [Theory]
    // Trimming a path of nothing but separators would leave the empty string, which is not a spelling
    // of a root here - it is how a destination says "the file's own library path".
    [InlineData("/")]
    [InlineData(@"\")]
    [InlineData("//")]
    public void ARootOfNothingButSeparators_KeepsASpellingOfItsOwn(string given) =>
        Assert.Equal(["/"], CoveRenamerDataPort.ReadLibraryRoots(ConfigWith(given)));

    [Fact]
    public void TwoSpellingsOfOneFolder_ArriveAsOneRoot_NotTwo() =>
        Assert.Equal(
            ["C:/Videos", "C:/Videos"],
            CoveRenamerDataPort.ReadLibraryRoots(ConfigWith(@"C:\Videos\", "C:/Videos")));

    [Fact]
    public void ABlankEntryIsDropped_SoItCannotAnchorARule() =>
        Assert.Equal(
            ["/data"],
            CoveRenamerDataPort.ReadLibraryRoots(ConfigWith("", "   ", "/data")));

    [Fact]
    public void AbsentConfiguration_IsAnEmptyList_NotAThrow() =>
        Assert.Empty(CoveRenamerDataPort.ReadLibraryRoots(null));
}
