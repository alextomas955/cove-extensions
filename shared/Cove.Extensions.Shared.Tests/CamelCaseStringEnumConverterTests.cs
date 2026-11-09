using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cove.Extensions.Shared.Tests;

public sealed class CamelCaseStringEnumConverterTests
{
    [JsonConverter(typeof(CamelCaseStringEnumConverter))]
    public enum ToyStatus
    {
        Rename,
        SkipTooLong,
    }

    private sealed record ToyResponse(ToyStatus Status);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // A converter of the same kind declared on an options object, which the precedence case below
    // uses to show that it outranks the type attribute rather than duplicating it.
    private static readonly JsonSerializerOptions OptionsLevelConverter =
        new() { Converters = { new JsonStringEnumConverter() } };

    // The wire spelling every extension's responses are read against. Asserted as the literal text a
    // UI compares with: a round trip is symmetric and agrees with itself whichever spelling is
    // written, while a numeric or PascalCase value reads as no match and the action silently never
    // fires.
    [Theory]
    [InlineData(ToyStatus.Rename, "rename")]
    [InlineData(ToyStatus.SkipTooLong, "skipTooLong")]
    public void AnEnumDeclaringTheConverter_IsWrittenInCamelCase(ToyStatus value, string expected)
    {
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void TheAttributeApplies_WhereverTheEnumIsReached_IncludingAsAMemberOfAResponse()
    {
        Assert.Equal(
            """{"status":"skipTooLong"}""",
            JsonSerializer.Serialize(
                new ToyResponse(ToyStatus.SkipTooLong), Web));
    }

    // Requests bind case-insensitively, so a caller may send either spelling back.
    [Theory]
    [InlineData("\"skipTooLong\"")]
    [InlineData("\"SkipTooLong\"")]
    public void EitherSpelling_ReadsBack(string json)
    {
        Assert.Equal(ToyStatus.SkipTooLong, JsonSerializer.Deserialize<ToyStatus>(json));
    }

    // The documented precedence, which is why no extension declares an equivalent converter on an
    // options object: one there outranks the attribute instead of duplicating it, so a copy that
    // drifted would win without a word.
    [Fact]
    public void AConverterOnTheOptions_OutranksTheAttribute()
    {
        Assert.Equal(
            "\"SkipTooLong\"", JsonSerializer.Serialize(ToyStatus.SkipTooLong, OptionsLevelConverter));
    }
}
