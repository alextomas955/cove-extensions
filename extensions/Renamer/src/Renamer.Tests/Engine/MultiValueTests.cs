using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests.Engine;

public class MultiValueTests
{
    // ---- MultiValue.Resolve ----

    private static readonly IReadOnlyList<string> Three = new[] { "Charlie", "alice", "Bob" };

    [Fact]
    public void Resolve_EmptyList_ReturnsEmpty()
    {
        var m = new MultiValueOptions();
        Assert.Equal("", MultiValue.Resolve(Array.Empty<string>(), m));
    }

    [Fact]
    public void Resolve_SortNoneUnlimited_JoinsInInputOrderWithTheSeparator()
    {
        var m = new MultiValueOptions { Separator = " | ", Sort = SortOrder.None };
        Assert.Equal("Charlie | alice | Bob", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_SortNameAsc_OrdersCaseInsensitively()
    {
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.NameAsc };
        Assert.Equal("alice,Bob,Charlie", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_NameOnlyList_IsNotFilteredByTheIdWhitelistOrBlacklist()
    {
        // A name carries no id, so neither id list can be tested against it. Reading "not in the
        // whitelist" as a match failure would empty the $tags token of every preview sample.
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.None,
            WhitelistIds = [1],
            BlacklistIds = [2],
        };

        Assert.Equal("Charlie,alice,Bob", MultiValue.Resolve(Three, m));
    }

    // The whitelist and blacklist match on tag ids, never on names.
    private static readonly IReadOnlyList<(int Id, string Name)> ThreeTags =
        new[] { (1, "Charlie"), (2, "alice"), (3, "Bob") };

    [Fact]
    public void Resolve_WhitelistIds_KeepsOnlyListedTags()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.None,
            WhitelistIds = [2, 3],
        };
        Assert.Equal("alice,Bob", MultiValue.Resolve(ThreeTags, m));
    }

    [Fact]
    public void Resolve_BlacklistIds_DropsListedTags()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.None,
            BlacklistIds = [3],
        };
        Assert.Equal("Charlie,alice", MultiValue.Resolve(ThreeTags, m));
    }

    [Fact]
    public void Resolve_EverythingFilteredOut_ReturnsEmpty()
    {
        var m = new MultiValueOptions { WhitelistIds = [999] };
        Assert.Equal("", MultiValue.Resolve(ThreeTags, m));
    }

    [Fact]
    public void Resolve_TagRefs_EmptyList_ReturnsEmpty()
    {
        var m = new MultiValueOptions { Separator = ",", WhitelistIds = [1] };

        Assert.Equal("", MultiValue.Resolve(Array.Empty<(int, string)>(), m));
    }

    [Fact]
    public void Resolve_TagRefs_DefaultOptions_RenderNamesInNameOrder()
    {
        var m = new MultiValueOptions { Separator = ", " };

        Assert.Equal("alice, Bob, Charlie", MultiValue.Resolve(ThreeTags, m));
    }

    [Fact]
    public void Resolve_OverflowKeepFirst_TakesFirstN()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.None,
            MaxCount = 2,
            OnOverflow = OverflowPolicy.KeepFirst,
        };
        Assert.Equal("Charlie,alice", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_OverflowDropAll_ReturnsEmpty()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.None,
            MaxCount = 2,
            OnOverflow = OverflowPolicy.DropAll,
        };
        Assert.Equal("", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_CountEqualsMaxCount_NoOverflow()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.None,
            MaxCount = 3,
            OnOverflow = OverflowPolicy.DropAll,
        };
        Assert.Equal("Charlie,alice,Bob", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_SortThenKeepFirst_TakesFirstAfterSort()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.NameAsc,
            MaxCount = 2,
            OnOverflow = OverflowPolicy.KeepFirst,
        };
        // sorted: alice,Bob,Charlie -> take first 2
        Assert.Equal("alice,Bob", MultiValue.Resolve(Three, m));
    }

    // ---- MultiValue.Resolve (performer records: id/favorite sort + gender order/ignore) ----

    private static readonly IReadOnlyList<RenamerPerformer> Performers = new[]
    {
        new RenamerPerformer(3, "Charlie", Favorite: false, Gender: "Male"),
        new RenamerPerformer(1, "alice", Favorite: true, Gender: "Female"),
        new RenamerPerformer(2, "Bob", Favorite: false, Gender: "Male"),
    };

    [Fact]
    public void Resolve_Performers_WhitelistIds_KeepsOnlyListedIds_AndRendersNames()
    {
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.None, WhitelistIds = [1, 2] };
        Assert.Equal("alice,Bob", MultiValue.Resolve(Performers, m));
    }

    [Fact]
    public void Resolve_Performers_BlacklistIds_DropsListedIds()
    {
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.None, BlacklistIds = [2] };
        Assert.Equal("Charlie,alice", MultiValue.Resolve(Performers, m));
    }

    [Fact]
    public void Resolve_Performers_SortById_OrdersByAscendingId()
    {
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.IdAsc };
        // ids 1,2,3 -> alice,Bob,Charlie
        Assert.Equal("alice,Bob,Charlie", MultiValue.Resolve(Performers, m));
    }

    [Fact]
    public void Resolve_Performers_FavoriteFirst_PutsFavoritesFirstThenByName()
    {
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.FavoriteFirst };
        // alice is the only favorite, then the rest by name: Bob, Charlie
        Assert.Equal("alice,Bob,Charlie", MultiValue.Resolve(Performers, m));
    }

    [Fact]
    public void Resolve_Performers_IgnoreGender_FreesAnOverflowSlot()
    {
        // Three performers, a limit of 2, one gender ignored. The ignored performer is dropped
        // before the limit, so two non-ignored performers survive (not one).
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.NameAsc,
            MaxCount = 2,
            OnOverflow = OverflowPolicy.KeepFirst,
            IgnoreGenders = ["Female"],
        };

        var result = MultiValue.Resolve(Performers, m);

        // Female (alice) is dropped first; the two males survive the limit, name-ordered.
        Assert.Equal("Bob,Charlie", result);
        Assert.Equal(2, result.Split(',').Length);
    }

    [Fact]
    public void Resolve_Performers_IgnoreGender_IsCaseInsensitive_AndKeepsNullGender()
    {
        var people = new[]
        {
            new RenamerPerformer(1, "alice", false, "Female"),
            new RenamerPerformer(2, "Bob", false, null),     // no gender set -> always kept
            new RenamerPerformer(3, "Charlie", false, "male"),
        };
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.NameAsc, IgnoreGenders = ["MALE"] };

        // "male" Charlie dropped (case-insensitive); null-gender Bob kept; alice kept.
        Assert.Equal("alice,Bob", MultiValue.Resolve(people, m));
    }

    [Fact]
    public void Resolve_Performers_GenderOrder_ReordersByConfiguredRank()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.NameAsc,
            GenderOrder = ["Male", "Female"],
        };

        // Name order would be alice,Bob,Charlie; the gender rank puts Males first (Bob,Charlie)
        // then Females (alice), each group keeping the name order.
        Assert.Equal("Bob,Charlie,alice", MultiValue.Resolve(Performers, m));
    }

    [Fact]
    public void Resolve_Performers_GenderOrder_UnlistedAndNullGenderSortLast()
    {
        var people = new[]
        {
            new RenamerPerformer(1, "alice", false, "Female"),
            new RenamerPerformer(2, "Bob", false, null),
            new RenamerPerformer(3, "Charlie", false, "Other"),
        };
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.NameAsc, GenderOrder = ["Female"] };

        // Female (alice) ranks first; Bob (null) and Charlie ("Other", unlisted) rank last,
        // keeping their name order.
        Assert.Equal("alice,Bob,Charlie", MultiValue.Resolve(people, m));
    }

    [Fact]
    public void Resolve_Performers_DefaultOptions_RenderNamesLikeTheStringPath()
    {
        // Regression guard: with default options (NameAsc, no gender features) the record path
        // produces the same joined names as the equivalent string list would.
        var m = new MultiValueOptions { Separator = ", " };
        Assert.Equal("alice, Bob, Charlie", MultiValue.Resolve(Performers, m));
    }
}
