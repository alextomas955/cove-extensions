using Renamer.Engine;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests;

public class MultiValueTests
{
    // ---- ResolutionLabel.FromHeight ----

    [Theory]
    [InlineData(2160, "4k")]
    [InlineData(1440, "1440p")]
    [InlineData(1080, "1080p")]
    [InlineData(720, "720p")]
    [InlineData(480, "480p")]
    public void ResolutionLabel_BoundaryValues_MapToBucket(int height, string expected)
    {
        Assert.Equal(expected, ResolutionLabel.FromHeight(height));
    }

    [Theory]
    [InlineData(3000, "4k")]   // > 2160
    [InlineData(2000, "1440p")] // between 1440 and 2160
    [InlineData(1200, "1080p")]
    [InlineData(900, "720p")]
    [InlineData(600, "480p")]
    public void ResolutionLabel_AboveBucket_MapsToNearestLowerLabel(int height, string expected)
    {
        Assert.Equal(expected, ResolutionLabel.FromHeight(height));
    }

    [Theory]
    [InlineData(360, "360")]
    [InlineData(240, "240")]
    [InlineData(0, "0")]
    public void ResolutionLabel_BelowMinBucket_ReturnsRawHeight(int height, string expected)
    {
        Assert.Equal(expected, ResolutionLabel.FromHeight(height));
    }

    // ---- MultiValue.Resolve ----

    private static readonly IReadOnlyList<string> Three = new[] { "Charlie", "alice", "Bob" };

    [Fact]
    public void Resolve_EmptyList_ReturnsEmpty()
    {
        var m = new MultiValueOptions();
        Assert.Equal("", MultiValue.Resolve(Array.Empty<string>(), m));
    }

    [Fact]
    public void Resolve_Separator_JoinsWithSeparator()
    {
        var m = new MultiValueOptions { Separator = " | ", Sort = SortOrder.None };
        Assert.Equal("Charlie | alice | Bob", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_SortNone_PreservesInputOrder()
    {
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.None };
        Assert.Equal("Charlie,alice,Bob", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_SortNameAsc_OrdersCaseInsensitively()
    {
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.NameAsc };
        Assert.Equal("alice,Bob,Charlie", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_Whitelist_KeepsOnlyListedCaseInsensitive()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.None,
            Whitelist = ["ALICE", "bob"],
        };
        Assert.Equal("alice,Bob", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_Blacklist_DropsListedCaseInsensitive()
    {
        var m = new MultiValueOptions
        {
            Separator = ",",
            Sort = SortOrder.None,
            Blacklist = ["BOB"],
        };
        Assert.Equal("Charlie,alice", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_EverythingFilteredOut_ReturnsEmpty()
    {
        var m = new MultiValueOptions { Whitelist = ["nobody"] };
        Assert.Equal("", MultiValue.Resolve(Three, m));
    }

    [Fact]
    public void Resolve_MaxCountZero_IsUnlimited()
    {
        var m = new MultiValueOptions { Separator = ",", Sort = SortOrder.None, MaxCount = 0 };
        Assert.Equal("Charlie,alice,Bob", MultiValue.Resolve(Three, m));
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
        // BEFORE the limit, so two non-ignored performers survive (not one).
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

    [Fact]
    public void SortOrder_HasNoRatingValue_PerformerRatingDeferred_NoPrincipal()
    {
        // Performer sort-by-rating is intentionally NOT offered: rating is per-user data and the
        // detached renamer job runs without a signed-in user, so there is no defined rating to order
        // by. This negative assertion documents and guards that deferral — if someone adds a rating
        // sort, they must revisit the no-principal source decision first.
        Assert.False(
            Enum.GetNames<SortOrder>().Any(n => n.Contains("Rating", StringComparison.OrdinalIgnoreCase)),
            "performer sort-by-rating is deferred (no principal in the detached job); do not add a rating SortOrder without revisiting the source decision.");
    }
}
