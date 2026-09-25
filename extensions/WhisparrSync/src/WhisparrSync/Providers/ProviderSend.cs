using System.Net;
using System.Text.Json;

namespace WhisparrSync.Providers;

// A refusal is the provider's own answer and re-sending only collects it again, so the two nulls
// are held apart by WasDefinite: one is worth another attempt and one is not.
internal readonly record struct ProviderSend(JsonElement? Body, bool WasDefinite)
{
    internal static ProviderSend Nothing => new(null, WasDefinite: false);

    internal static ProviderSend Refused => new(null, WasDefinite: true);

    internal static ProviderSend Carrying(JsonElement body) => new(body, WasDefinite: true);

    // A 4xx is the provider's own answer about the request. A 408, a 429 and every 5xx are the
    // shapes a proxy, a rate limiter and an overloaded host take, so those are retried.
    internal static ProviderSend From(HttpStatusCode status)
        => (int)status is >= 400 and < 500
            && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                ? Refused
                : Nothing;
}
