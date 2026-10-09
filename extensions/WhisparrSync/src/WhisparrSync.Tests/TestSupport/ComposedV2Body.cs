using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Whisparr2.Net;
using Whisparr2.Net.Client;

namespace WhisparrSync.Tests.TestSupport;

// Serialised with the v2 generated client's own options, read from its own registration, so a test
// asserts what the instance would send. Options restated here would agree with the test, not the
// send.
internal static class ComposedV2Body
{
    private static readonly JsonSerializerOptions WireOptions = ReadWireOptions();

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
