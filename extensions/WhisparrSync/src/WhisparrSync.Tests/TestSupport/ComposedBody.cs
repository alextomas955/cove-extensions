using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Whisparr3.Net;
using Whisparr3.Net.Client;

namespace WhisparrSync.Tests.TestSupport;

// Serialised with the generated client's own options, read from its own registration, so a test
// asserts what the instance would send. Options restated here would agree with the test, not the
// send.
internal static class ComposedBody
{
    private static readonly JsonSerializerOptions WireOptions = ReadWireOptions();

    public static JsonObject Of<TResource>(TResource resource)
        => JsonNode.Parse(JsonSerializer.Serialize(resource, WireOptions)) as JsonObject
            ?? throw new InvalidOperationException("A composed body serialised to no JSON object.");

    private static JsonSerializerOptions ReadWireOptions()
    {
        var services = new ServiceCollection();
        services.AddWhisparr3(new Whisparr3Options
        {
            BaseUrl = "http://127.0.0.1",
            ApiKey = "unused",
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<JsonSerializerOptionsProvider>().Options;
    }
}
