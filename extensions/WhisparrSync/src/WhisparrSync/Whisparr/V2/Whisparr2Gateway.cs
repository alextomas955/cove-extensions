using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Whisparr2.Net;
using Whisparr2.Net.Client;

namespace WhisparrSync.Whisparr;

// Whisparr v2's request surface, from the generated Whisparr 2 client. The v2 and v3 packages
// declare unrelated IApiResponse interfaces and unrelated option types, so one type cannot serve
// both and this is a twin of Whisparr3Gateway.
//
// The redirect cap, the read bound and the target's budget are applied to every typed client the
// registration creates. None is the generated client's default; each is stated on WhisparrTransport.
internal sealed class Whisparr2Gateway : IDisposable
{
    private readonly GeneratedClientRegistry<Whisparr2Target> _registry;

    public Whisparr2Gateway(
        Func<HttpMessageHandler>? primaryHandler = null,
        Action<HttpClient>? configure = null)
    {
        var handler = primaryHandler ?? WhisparrTransport.CreateHandler;
        // No default settings: the timeout comes from the target's budget below, and a supplied
        // configure runs after it so a caller can override.
        var settings = configure ?? (static _ => { });
        _registry = new GeneratedClientRegistry<Whisparr2Target>(
            target => Register(target, handler, settings));
    }

    public Whisparr2Apis For(Whisparr2Target target) => new(_registry.Reach(target));

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
        Whisparr2Target target, Func<HttpMessageHandler> handler, Action<HttpClient> settings)
    {
        var services = new ServiceCollection();
        services.AddWhisparr2(new Whisparr2Options
        {
            BaseUrl = target.BaseAddress.ToString(),
            ApiKey = target.ApiKey,
            ConfigureHttpClient = builder => builder
                .ConfigurePrimaryHttpMessageHandler(handler)
                .AddHttpMessageHandler(static () =>
                    new BoundedResponseHandler(WhisparrTransport.MaxResponseBytes))
                .ConfigureHttpClient(client => client.Timeout = target.Budget)
                .ConfigureHttpClient(settings),
        });

        return services.BuildServiceProvider();
    }
}

// The whole record is the registry cache key by value. The key is part of it because a key edited
// against the same address is a different registration. The budget is part of it because
// HttpClient.Timeout cannot change once a client has been used, so a second budget against the same
// instance needs a second client.
internal readonly record struct Whisparr2Target(Uri BaseAddress, string ApiKey, TimeSpan Budget)
{
    public Whisparr2Target(Uri baseAddress, string apiKey)
        : this(baseAddress, apiKey, WhisparrTransport.RequestTimeout)
    {
    }
}

// Bound to one instance rather than taking it per call, so a call site cannot name one instance for
// the read and another for the write that follows it. Held open for as long as the request it serves
// is sending, so the registration behind it is not discarded from under that request.
internal sealed class Whisparr2Apis(
    GeneratedClientRegistry<Whisparr2Target>.Lease lease) : IDisposable
{
    public TApi Api<TApi>()
        where TApi : notnull
        => lease.Provider.GetRequiredService<TApi>();

    public void Dispose() => lease.Dispose();
}
