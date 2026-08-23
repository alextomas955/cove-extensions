using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// Pins that an existing persisted <c>RenamerOptions</c> blob — one whose properties AND enum values hold
/// the byte-literal PascalCase spelling a shipped v0.1.0 install wrote — still deserializes to the correct
/// <see cref="RenamerOptions"/> through <see cref="RenamerOptions.JsonOptions"/>, the persisted-blob
/// serializer <c>OptionsStore</c> reuses unchanged. The Cove-facing response wire re-cased to camelCase in
/// this wave, but the options blob keeps its own PascalCase spelling independently (it is stored via the
/// host data key, not re-serialized), so the flip must never orphan a user's stored options. The enum
/// spellings are hard-coded strings (not <c>nameof</c>) precisely because they model the on-disk bytes,
/// which stay frozen even though the C# identifiers keep PascalCase.
/// </summary>
[Trait("Tier", "L0")]
public sealed class PersistedRenamerOptionsRoundTripTests
{
    [Fact]
    public void OldPascalCaseBlob_WithPascalCaseEnumValues_StillLoads()
    {
        // A blob shaped exactly as the shipped panel wrote it: PascalCase property names and PascalCase
        // enum values (Case, the multi-value Sort / OnOverflow) — plus the two default-relocate keys a
        // real install's stored blob still carries after they were retired from the record. Those keys
        // are what makes this case the unknown-key tolerance proof: a stored blob from before the
        // deletion must still load, with the retired keys ignored and every surviving option intact.
        const string blob =
            """
            {
              "FilenameTemplate": "$title",
              "Case": "Title",
              "Performers": { "Separator": " ", "Sort": "FavoriteFirst", "OnOverflow": "KeepFirst" },
              "Tags": { "Sort": "NameAsc" },
              "RequiredFields": ["title"],
              "DefaultDestination": "D:/sorted",
              "EnableDefaultRelocate": true,
              "FilenameMax": 200
            }
            """;

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(blob, RenamerOptions.JsonOptions)!;

        Assert.Equal("$title", loaded.FilenameTemplate);
        Assert.Equal(CaseTransform.Title, loaded.Case);
        Assert.Equal(SortOrder.FavoriteFirst, loaded.Performers.Sort);
        Assert.Equal(OverflowPolicy.KeepFirst, loaded.Performers.OnOverflow);
        Assert.Equal(SortOrder.NameAsc, loaded.Tags.Sort);
        // The tolerance proof, and why FilenameMax is read LAST in the blob rather than first: the two
        // retired keys sit immediately before it, so this assertion fails if the loader stops, throws or
        // skips ahead on an unknown key. A modeled value read only from BEFORE them would pass either way.
        Assert.Equal(200, loaded.FilenameMax);
    }
}
