using System.Text.Json;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Options;

/// <summary>
/// The destination half of the one-time options conversion: a typed absolute root becomes a Cove library
/// path plus the relative template rendered under it. Every case here is a way a user's routing changes
/// without them touching it, so each asserts the rule that survives beside the one that does not.
/// </summary>
public sealed class OptionsMigrationDestinationTests
{
    // ContainingRoot resolves a stored root against the platform's path rules, so a drive-letter
    // fixture answers differently on a Linux runner than a rooted one does.
    private static readonly string Media = OperatingSystem.IsWindows() ? "G:/media" : "/media";
    private static readonly string Archive = OperatingSystem.IsWindows() ? "I:/archive" : "/archive";
    private static readonly string Elsewhere = OperatingSystem.IsWindows() ? "E:/elsewhere" : "/elsewhere";

    private static readonly string[] LibraryPaths = [Media, Archive];

    /// <summary>The stored blob a real install has: one rule under each library path, and one under none.</summary>
    /// <param name="unorganized">
    /// The unorganized route as stored. It is the one member whose EMPTY value means "there is no route"
    /// rather than "this rule names no root of its own", so the two cases differ in exactly this member.
    /// </param>
    private static string Blob(string unorganized) =>
        $$"""
        {
          "FolderTemplate": "$studio",
          "StudioDestinations": { "101": {{Quoted(Native($"{Archive}/videos"))}} },
          "PathDestinations": [
            { "Pattern": {{Quoted(Native($"{Media}/in"))}}, "Dest": {{Quoted(Native($"{Media}/videos"))}}, "IsRegex": false },
            { "Pattern": {{Quoted(Native($"{Media}/junk"))}}, "Dest": {{Quoted(Native($"{Elsewhere}/off"))}}, "IsRegex": false }
          ],
          "UnorganizedDestination": {{Quoted(unorganized)}}
        }
        """;

    private static readonly string Configured = Blob(Native($"{Media}/unsorted"));

    /// <summary>Spelled with the platform separator, which is how the pre-conversion panel wrote a root.</summary>
    private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string Quoted(string value) => JsonSerializer.Serialize(value);

    private static RenamerOptions Reload(string json) =>
        JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions)!;

    [Fact]
    public void AStoredRoot_BecomesTheLibraryPathHoldingIt_PlusTheRestAndTheOldFolderTemplate()
    {
        // The arithmetic is the whole claim: a matched rule used to land an item at its stored root with
        // the global folder template rendered underneath, so the same folder is (the library path holding
        // that root) + (the rest of that root)/(that same template). Get it wrong and every item the rule
        // matches moves on the first run after the conversion.
        var converted = OptionsMigration.ConvertDestinationsToRoots(Configured, LibraryPaths);
        var options = Reload(converted.Json);

        Assert.False(converted.Deferred);
        Assert.Equal(Dest.At(Archive, "videos/$studio"), options.StudioDestinations[101]);
        Assert.Equal(Dest.At(Media, "unsorted/$studio"), options.UnorganizedDestination);

        // The default is left exactly as stored: FolderRoot's own default is the file's own library path,
        // which is what a relative template has always been measured from.
        Assert.Equal("$studio", options.FolderTemplate);
        Assert.Equal(string.Empty, options.FolderRoot);
    }

    [Fact]
    public void AStoredRootUnderNoLibraryPath_IsDropped_AndNamedInTheTrail()
    {
        // There is no root to choose for such a rule and inventing one would relocate files, so it goes.
        // Its items follow the default afterwards, which is a behaviour change the user did not ask for,
        // and the trail is the only place that is visible.
        var converted = OptionsMigration.ConvertDestinationsToRoots(Configured, LibraryPaths);
        var options = Reload(converted.Json);

        var dropped = Assert.Single(converted.Dropped);
        Assert.Equal("PathDestinations[1]", dropped.Rule);
        Assert.Equal(Native($"{Elsewhere}/off"), dropped.Stored);

        // The surviving rule is asserted beside it: a conversion that dropped every path rule would
        // satisfy the lines above on its own.
        var kept = Assert.Single(options.PathDestinations);
        Assert.Equal(Native($"{Media}/in"), kept.Pattern);
        Assert.Equal(Dest.At(Media, "videos/$studio"), kept.Dest);
    }

    [Fact]
    public void AnEmptyUnorganizedDestination_IsRemoved_SoTheWholeBlobStillBinds()
    {
        // The failure here is total and silent: a bare JSON string cannot bind to Destination, so a
        // conversion that leaves the member in place makes the WHOLE blob throw on load, and the options
        // store answers a throw with defaults - every setting the user configured reads as unset with
        // nothing anywhere saying why. Removing the member is what makes the ABSENT key mean what the
        // empty value always meant: there is no unorganized route.
        var converted = OptionsMigration.ConvertDestinationsToRoots(Blob(string.Empty), LibraryPaths);
        var options = Reload(converted.Json);

        Assert.Null(options.UnorganizedDestination);
        using (var raw = JsonDocument.Parse(converted.Json))
        {
            Assert.False(raw.RootElement.TryGetProperty("UnorganizedDestination", out _));
        }

        // Asserted beside it because a conversion that discarded every destination would satisfy the two
        // claims above: the rest of the blob converts exactly as it does with a route configured.
        Assert.Equal(Dest.At(Archive, "videos/$studio"), options.StudioDestinations[101]);
        Assert.Equal(Dest.At(Media, "videos/$studio"), Assert.Single(options.PathDestinations).Dest);

        // And the removal counts as a change on its own. Without that, a blob whose only legacy shape is
        // this member is reported as nothing-to-do, and the unbindable value stays in the store forever.
        var alone = OptionsMigration.ConvertDestinationsToRoots(
            """{ "UnorganizedDestination": "" }""", LibraryPaths);

        Assert.True(alone.Changed);
        Assert.False(alone.Deferred);
        Assert.Null(Reload(alone.Json).UnorganizedDestination);
    }

    [Fact]
    public void WithNoLibraryPathsAtAll_ItConvertsNothing_AndSaysSo()
    {
        // The same safety argument the name-to-id half makes about an empty entity table: an empty list is
        // indistinguishable from a host that has not supplied one yet, and converting against it would
        // drop EVERY rule the user has.
        var converted = OptionsMigration.ConvertDestinationsToRoots(Configured, []);

        Assert.True(converted.Deferred);
        Assert.False(converted.Changed);
        Assert.Equal(Configured, converted.Json);
    }

    [Fact]
    public void AKeyThisHalfDoesNotModel_SurvivesTheRewriteVerbatim()
    {
        // The name-to-id half is covered for this; the destination half rewrites through a second parse
        // and write of the same document, so it can lose a hand-edited or newer-version key on its own.
        var converted = OptionsMigration.ConvertDestinationsToRoots(
            $$"""
            {
              "FolderTemplate": "$studio",
              "SomethingThisConverterNeverHeardOf": { "deep": [1, 2] },
              "StudioDestinations": { "101": {{Quoted(Native($"{Archive}/videos"))}} }
            }
            """,
            LibraryPaths);

        Assert.Equal(Dest.At(Archive, "videos/$studio"), Reload(converted.Json).StudioDestinations[101]);

        using var raw = JsonDocument.Parse(converted.Json);
        Assert.Equal(
            "[1,2]",
            raw.RootElement.GetProperty("SomethingThisConverterNeverHeardOf").GetProperty("deep").GetRawText());
    }
}
