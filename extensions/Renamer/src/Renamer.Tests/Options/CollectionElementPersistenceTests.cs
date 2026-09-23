using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class CollectionElementPersistenceTests
{
    private static RenamerOptions RoundTrip(RenamerOptions original)
    {
        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        return JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions)!;
    }

    [Fact]
    public void FieldReplaceRule_KeepsEveryMember()
    {
        var reloaded = RoundTrip(new RenamerOptions
        {
            FieldReplacers =
            [
                new FieldReplaceRule { TargetToken = "title", Find = "find-me", Replace = "replace-me" },
            ],
        });

        var rule = Assert.Single(reloaded.FieldReplacers);
        Assert.Equal("title", rule.TargetToken);
        Assert.Equal("find-me", rule.Find);
        Assert.Equal("replace-me", rule.Replace);
    }

    [Fact]
    public void PathDestinationRule_KeepsEveryMember_IncludingItsDestination()
    {
        var reloaded = RoundTrip(new RenamerOptions
        {
            PathDestinations =
            [
                new PathDestinationRule
                {
                    Pattern = "incoming/.*",
                    IsRegex = true,
                    Dest = new Destination { Root = "/library", Template = "$studio/$year" },
                },
            ],
        });

        var rule = Assert.Single(reloaded.PathDestinations);
        Assert.Equal("incoming/.*", rule.Pattern);
        Assert.True(rule.IsRegex);
        Assert.Equal("/library", rule.Dest.Root);
        Assert.Equal("$studio/$year", rule.Dest.Template);
    }

    [Fact]
    public void ExcludeRule_KeepsEveryMember()
    {
        var reloaded = RoundTrip(new RenamerOptions
        {
            ExcludePaths = [new ExcludeRule { Pattern = "protected/.*", IsRegex = true }],
        });

        var rule = Assert.Single(reloaded.ExcludePaths);
        Assert.Equal("protected/.*", rule.Pattern);
        Assert.True(rule.IsRegex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADestinationMapValue_KeepsEveryMember(bool byStudio)
    {
        var destination = new Destination { Root = "/library", Template = "$studio/$year" };
        var reloaded = RoundTrip(byStudio
            ? new RenamerOptions { StudioDestinations = new() { [7] = destination } }
            : new RenamerOptions { TagDestinations = new() { [7] = destination } });

        var map = byStudio ? reloaded.StudioDestinations : reloaded.TagDestinations;
        var stored = Assert.Contains(7, map);
        Assert.Equal("/library", stored.Root);
        Assert.Equal("$studio/$year", stored.Template);
    }

    [Fact]
    public void AKindMapValue_KeepsItsSwitch_AndEveryMemberOfItsDestination()
    {
        var reloaded = RoundTrip(new RenamerOptions
        {
            Kinds = new()
            {
                [RenamerFileKind.Image] = new KindOptions
                {
                    Enabled = false,
                    Destination = new Destination { Root = "/images", Template = "$studio" },
                },
            },
        });

        var kind = Assert.Contains(RenamerFileKind.Image, reloaded.Kinds);
        Assert.False(kind.Enabled);
        Assert.NotNull(kind.Destination);
        Assert.Equal("/images", kind.Destination.Root);
        Assert.Equal("$studio", kind.Destination.Template);
    }
}
