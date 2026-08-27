using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Options;

/// <summary>
/// Pins the semantics of <see cref="RenamerOptions"/>' structural equality (the dirty-check the settings
/// UI relies on) after folding the hand-rolled twin-list Equals/GetHashCode into a single
/// EqualityComponents source. Two instances with the same field VALUES must be Equal (with equal hash)
/// even though their collection members are distinct instances; any single collection-member difference
/// — including a <see cref="MultiValueOptions"/> member — must flip equality. This is the exact footgun
/// (45-R4): a collection member that compared by reference would break the save/load round-trip.
/// </summary>
public sealed class RenamerOptionsEqualityTests
{
    [Fact]
    public void FreshInstances_SameValues_AreEqual_WithEqualHash()
    {
        var a = new RenamerOptions();
        var b = new RenamerOptions();

        // Distinct list/dictionary instances with identical contents (defaults include DropOrder,
        // RequiredFields, Articles, and the Performers/Tags MultiValueOptions) must still be value-equal.
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void PopulatedCollections_FreshInstances_SameContents_AreEqual_WithEqualHash()
    {
        RenamerOptions Make() => new()
        {
            DropOrder = ["studio", "date"],
            AllowedRoots = ["/media/a", "/media/b"],
            StudioDestinations = new() { [1] = Dest.At("/x"), [2] = Dest.At("/y") },
            TagDestinations = new() { [31] = Dest.At("/anime") },
            PathDestinations = [new PathDestinationRule { Pattern = "p", Dest = Dest.At("/d") }],
            Performers = new() { WhitelistIds = [11, 12] },
        };

        Assert.Equal(Make(), Make());
        Assert.Equal(Make().GetHashCode(), Make().GetHashCode());
    }

    [Fact]
    public void ListMemberDiffers_NotEqual()
    {
        var baseline = new RenamerOptions { DropOrder = ["studio", "date"] };

        Assert.NotEqual(baseline, baseline with { DropOrder = ["date", "studio"] });   // order-sensitive
        Assert.NotEqual(baseline, baseline with { DropOrder = ["studio"] });           // length
        Assert.NotEqual(new RenamerOptions(), new RenamerOptions { RequiredFields = ["title", "studio"] });
        Assert.NotEqual(new RenamerOptions(), new RenamerOptions { AllowedRoots = ["/media"] });
        Assert.NotEqual(new RenamerOptions(), new RenamerOptions { AssociatedExtensions = ["srt"] });
        Assert.NotEqual(
            new RenamerOptions(),
            new RenamerOptions { PathDestinations = [new PathDestinationRule { Pattern = "p", Dest = Dest.At("/d") }] });
        Assert.NotEqual(new RenamerOptions(), new RenamerOptions { ExcludeStudioIds = [5] });
    }

    [Fact]
    public void MultiValueOptionsMemberDiffers_NotEqual()
    {
        var a = new RenamerOptions { Performers = new() { Separator = " ", WhitelistIds = [11] } };
        var b = new RenamerOptions { Performers = new() { Separator = " ", WhitelistIds = [12] } };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TagDestinations_AreOrderIndependent()
    {
        var written = new RenamerOptions { TagDestinations = new() { [31] = Dest.At("/x"), [32] = Dest.At("/y") } };
        var reordered = new RenamerOptions { TagDestinations = new() { [32] = Dest.At("/y"), [31] = Dest.At("/x") } };

        Assert.Equal(written, reordered);
        Assert.Equal(written.GetHashCode(), reordered.GetHashCode());

        Assert.NotEqual(written, new RenamerOptions { TagDestinations = new() { [31] = Dest.At("/DIFFERENT"), [32] = Dest.At("/y") } });
    }

    [Fact]
    public void StudioDestinations_OrderIndependent_ValueSensitive()
    {
        var a = new RenamerOptions { StudioDestinations = new() { [1] = Dest.At("/x"), [2] = Dest.At("/y") } };
        var reordered = new RenamerOptions { StudioDestinations = new() { [2] = Dest.At("/y"), [1] = Dest.At("/x") } };

        Assert.Equal(a, reordered);
        Assert.Equal(a.GetHashCode(), reordered.GetHashCode());

        Assert.NotEqual(a, new RenamerOptions { StudioDestinations = new() { [1] = Dest.At("/x"), [2] = Dest.At("/DIFFERENT") } });
    }

    [Fact]
    public void ScalarMemberDiffers_NotEqual()
    {
        Assert.NotEqual(new RenamerOptions(), new RenamerOptions { FilenameTemplate = "$title" });
        Assert.NotEqual(new RenamerOptions(), new RenamerOptions { FilenameMax = 100 });
        Assert.NotEqual(new RenamerOptions(), new RenamerOptions { Case = CaseTransform.Lower });
        Assert.NotEqual(new RenamerOptions(), new RenamerOptions { FolderRoot = "/media" });
        Assert.NotEqual(
            new RenamerOptions(),
            new RenamerOptions { UnorganizedDestination = Dest.At("/media") });
    }
}
