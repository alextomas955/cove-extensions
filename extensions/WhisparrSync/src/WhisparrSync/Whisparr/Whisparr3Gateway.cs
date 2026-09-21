using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Whisparr3.Net;
using Whisparr3.Net.Client;

namespace WhisparrSync.Whisparr;

// Whisparr v3's request surface, from the generated Whisparr 3 client. That client fixes its
// address and key at registration and both are settings a person edits, so one registration is held
// per address-and-key pair rather than one per process.
//
// The redirect cap, the per-attempt timeout and the read bound are applied to every typed client the
// registration creates. None is the generated client's default; each is stated in WhisparrClient.
internal sealed class Whisparr3Gateway : IDisposable
{
    private readonly GeneratedClientRegistry<Whisparr3Target> _registry;

    public Whisparr3Gateway(
        Func<HttpMessageHandler>? primaryHandler = null,
        Action<HttpClient>? configure = null)
    {
        var handler = primaryHandler ?? WhisparrClient.CreateHandler;
        var settings = configure ?? WhisparrClient.Configure;
        _registry = new GeneratedClientRegistry<Whisparr3Target>(
            target => Register(target, handler, settings));
    }

    public Whisparr3Apis For(Whisparr3Target target) => new(_registry.Reach(target));

    // Status and body are taken as received. The generated accessor deserialises on exactly one
    // status and answers null on every other, which would lose the difference between a refusal and
    // an empty answer.
    public static WhisparrResponse Answered(IApiResponse answered)
    {
        ArgumentNullException.ThrowIfNull(answered);

        return new WhisparrResponse(
            (int)answered.StatusCode,
            ContentTypeOf(answered.ContentHeaders),
            answered.RawContent ?? string.Empty);
    }

    public void Dispose() => _registry.Dispose();

    private static string? ContentTypeOf(HttpContentHeaders? headers)
        => headers?.ContentType?.ToString();

    private static ServiceProvider Register(
        Whisparr3Target target, Func<HttpMessageHandler> handler, Action<HttpClient> settings)
    {
        var services = new ServiceCollection();
        services.AddWhisparr3(new Whisparr3Options
        {
            BaseUrl = target.BaseAddress.ToString(),
            ApiKey = target.ApiKey,
            ConfigureHttpClient = builder => builder
                .ConfigurePrimaryHttpMessageHandler(handler)
                .AddHttpMessageHandler(static () =>
                    new BoundedResponseHandler(WhisparrClient.MaxResponseBytes))
                .ConfigureHttpClient(settings),
        });

        return services.BuildServiceProvider();
    }
}

// The pair is the registry cache key by value: a key edited against the same address is a different
// registration.
internal readonly record struct Whisparr3Target(Uri BaseAddress, string ApiKey);

// Bound to one instance rather than taking it per call, so a call site cannot name one instance for
// the read and another for the write that follows it. Held open for as long as the request it serves
// is sending, so the registration behind it is not discarded from under that request.
internal sealed class Whisparr3Apis(
    GeneratedClientRegistry<Whisparr3Target>.Lease lease) : IDisposable
{
    public TApi Api<TApi>()
        where TApi : notnull
        => lease.Provider.GetRequiredService<TApi>();

    public void Dispose() => lease.Dispose();
}
