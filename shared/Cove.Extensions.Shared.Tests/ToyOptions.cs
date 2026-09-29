namespace Cove.Extensions.Shared.Tests;

// A small options model with one member of each kind the store's null restore distinguishes: a
// non-nullable string, collection and nested record, which take their default back, and a nullable
// member with a default, whose stored null is a real state.
public sealed record ToyOptions
{
    public string Name { get; init; } = "default-name";

    public List<string> Items { get; init; } = ["first", "second"];

    public ToyNested Nested { get; init; } = new();

    public string? Note { get; init; } = "default-note";

    public int Count { get; init; } = 3;
}

public sealed record ToyNested
{
    public string Label { get; init; } = "nested-label";

    public List<int> Ids { get; init; } = [7];
}
