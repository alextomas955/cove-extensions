using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// The transport half of step 1, measured rather than assumed.
/// </summary>
/// <remarks>
/// Nothing in the fixture ledger ever observed a request that produced no response, so "a connection
/// failure reads as unreachable" was an assumption until these ran. They open real sockets against a
/// port nothing listens on and a name that cannot resolve, which is the only way to settle it.
/// <para>
/// The client is built with the settings it ships with, so the timeout, the redirect cap and the
/// certificate policy under test are the ones a user gets.
/// </para>
/// </remarks>
public sealed class WhisparrClientTransportTests
{
    // Synthetic and authorises nothing: no instance is reached at either address below.
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    /// <summary>A refused connection.</summary>
    [Fact]
    public async Task AClosedPort_ReachesTheUnreachableKind()
    {
        var view = await TestAsync($"http://127.0.0.1:{ClosedLoopbackPort()}");

        Assert.Equal(ConnectionFailureKind.Unreachable, view.Kind);
        Assert.Null(view.Version);
    }

    /// <summary>
    /// A name that cannot resolve. The <c>.invalid</c> top-level domain is reserved as
    /// permanently unresolvable, so this asks the resolver a question with one right answer.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableHost_ReachesTheUnreachableKind()
    {
        var view = await TestAsync("http://no-such-whisparr-host.invalid:6969");

        Assert.Equal(ConnectionFailureKind.Unreachable, view.Kind);
        Assert.Null(view.Version);
    }

    /// <summary>
    /// An address a socket cannot be opened to is refused before any socket is opened, and reads as
    /// not configured rather than as unreachable: no request was made, so nothing was unreachable.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://whisparr:6969")]
    [InlineData("whisparr:6969")]
    [InlineData("not an address")]
    [InlineData("")]
    public async Task AnAddressNoSocketCanBeOpenedTo_IsRefusedWithoutARequest(string address)
        => Assert.Equal(ConnectionFailureKind.NotConfigured, (await TestAsync(address)).Kind);

    [Fact]
    public async Task NoKey_IsRefusedWithoutARequest()
        => Assert.Equal(
            ConnectionFailureKind.NotConfigured,
            (await NewTester().TestAsync("http://127.0.0.1:6969", "   ", TestContext.Current.CancellationToken)).Kind);

    /// <summary>
    /// The client refuses a scheme no socket may be opened to, rather than handing it to a handler
    /// that would act on it. The tester's own check keeps this unreachable from the route, so this
    /// drives the client directly.
    /// </summary>
    [Fact]
    public async Task TheClientItselfRefusesASchemeItCannotOpen()
    {
        using var http = NewHttpClient();
        var client = new WhisparrClient(http);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ReadStatusAsync(
                new Uri("file:///etc/passwd"), SomeKey, TestContext.Current.CancellationToken));
    }

    private static async Task<ConnectionTestView> TestAsync(string address)
        => await NewTester().TestAsync(address, SomeKey, TestContext.Current.CancellationToken);

    // The HttpClient outlives this call by design: it is handed to a client whose own lifetime is the
    // test's, and disposing it here would abort the request under test.
    private static ConnectionTester NewTester()
        => new ConnectionTester(new WhisparrClient(NewHttpClient()), NullLogger<ConnectionTester>.Instance);

    private static HttpClient NewHttpClient()
    {
        var http = new HttpClient(WhisparrClient.CreateHandler());
        WhisparrClient.Configure(http);
        return http;
    }

    // A port the operating system has just confirmed is free, then released. Asking for one is what
    // makes this a port nothing listens on rather than a number this file guessed at.
    private static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
