using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class KindOptionsJsonTests
{
    [Fact]
    public void KindsMap_SerializesWithTheSpellingThePanelWrites()
    {
        var options = new RenamerOptions
        {
            Kinds = new()
            {
                [RenamerFileKind.Text] = new KindOptions
                {
                    Enabled = false,
                    Destination = new Destination { Root = "/library", Template = "$studio" },
                },
            },
        };

        var json = JsonSerializer.Serialize(options, RenamerOptions.JsonOptions);

        using var parsed = JsonDocument.Parse(json);
        var kinds = parsed.RootElement.GetProperty("Kinds");
        var text = kinds.EnumerateObject().Single();

        // PascalCase, measured off the serializer: RenamerOptions.JsonOptions carries a
        // JsonStringEnumConverter, and an options-level converter overrides the camelCase attribute on
        // the enum type. The panel's hand-written blob uses these spellings.
        Assert.Equal("Text", text.Name);
        Assert.False(text.Value.GetProperty("Enabled").GetBoolean());
        Assert.Equal("/library", text.Value.GetProperty("Destination").GetProperty("Root").GetString());
    }

    [Fact]
    public void AKindStoredWithNoSettings_ReadsAsTheDefault()
    {
        // Valid JSON, and the store's non-null restore does not reach inside a collection, so the null
        // value arrives here. Read strictly it threw, taking the whole settings page with it.
        var options = JsonSerializer.Deserialize<RenamerOptions>(
            """{"Kinds":{"Text":null}}""", RenamerOptions.JsonOptions)!;

        Assert.True(options.IsKindEnabled(RenamerFileKind.Text));
        Assert.Null(options.KindDestination(RenamerFileKind.Text));
    }
}
