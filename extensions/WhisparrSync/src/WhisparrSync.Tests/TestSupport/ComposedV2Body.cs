using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Whisparr2.Net;
using Whisparr2.Net.Client;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>The JSON a composed older-generation request body becomes on the wire.</summary>
/// <remarks>
/// Serialised with that generation's generated client's own options, taken from its own registration,
/// so what a test asserts on is what the instance would receive. Options restated here would agree
/// with the test and not with the send.
/// </remarks>
internal static class ComposedV2Body
{
    private static readonly JsonSerializerOptions WireOptions = ReadWireOptions();

    /// <summary>What <paramref name="resource"/> serialises to.</summary>
    public static JsonObject Of<TResource>(TResource resource)
        => JsonNode.Parse(JsonSerializer.Serialize(resource, WireOptions)) as JsonObject
            ?? throw new InvalidOperationException("A composed body serialised to no JSON object.");

    private static JsonSerializerOptions ReadWireOptions()
    {
        var services = new ServiceCollection();
        services.AddWhisparr2(new Whisparr2Options
        {
            BaseUrl = "http://127.0.0.1",
            ApiKey = "unused",
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<JsonSerializerOptionsProvider>().Options;
    }
}
