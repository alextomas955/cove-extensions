using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class KindOptionsJsonTests
{
    [Fact]
    public void AKindStoredWithNoSettings_ReadsAsTheDefault()
    {
        // Valid JSON binding a present key to null, and the store's non-null restore does not reach
        // inside a collection, so the null arrives here. A strict read would throw and take the whole
        // settings page with it.
        var options = JsonSerializer.Deserialize<RenamerOptions>(
            """{"Kinds":{"Text":null}}""", RenamerOptions.JsonOptions)!;

        Assert.True(options.IsKindEnabled(RenamerFileKind.Text));
        Assert.Null(options.KindDestination(RenamerFileKind.Text));
    }
}
