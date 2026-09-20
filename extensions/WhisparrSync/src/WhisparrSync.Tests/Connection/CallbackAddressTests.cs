using System.Buffers.Text;
using System.Reflection;
using System.Security.Cryptography;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Connection;

public sealed class CallbackAddressTests
{
    private const string ExtensionId = "com.alextomas955.whisparrsync";

    // Without this the rest of the file would agree with any id literal, including one that names
    // no route this extension mounts.
    [Fact]
    public void TheIdTheseTestsUseIsTheOneTheExtensionShips()
        => Assert.Equal(ExtensionId, WhisparrSyncFixture.Manifest.Id);

    [Theory]
    [InlineData("http://cove:5073", "http://cove:5073")]
    [InlineData("https://media.example.com", "https://media.example.com")]
    [InlineData("http://cove:5073/", "http://cove:5073")]
    // The path prefix survives, so a Cove behind a reverse proxy on a subpath still produces a
    // working callback. A scheme-host-port reading would drop it.
    [InlineData("https://media.example.com/cove", "https://media.example.com/cove")]
    [InlineData("https://media.example.com/cove/", "https://media.example.com/cove")]
    [InlineData(
        "https://media.example.com/cove/api/extensions/com.alextomas955.whisparrsync/callback",
        "https://media.example.com/cove")]
    [InlineData(
        "https://media.example.com/cove/api/extensions/com.alextomas955.whisparrsync/callback?s=abc",
        "https://media.example.com/cove")]
    [InlineData(
        "http://host.docker.internal:5073/api/extensions/com.alextomas955.whisparrsync/callback?s=abc",
        "http://host.docker.internal:5073")]
    // A default port for the scheme reduces to one spelling, so a saved value does not depend on
    // how it was typed.
    [InlineData("http://cove:80/cove", "http://cove/cove")]
    public void TheMergeTakesSchemeHostPortAndPathPrefix(string edited, string expected)
        => Assert.Equal(expected, CallbackAddress.HostPartOf(edited, ExtensionId));

    [Theory]
    [InlineData("http://cove:5073")]
    [InlineData("https://media.example.com/cove/")]
    [InlineData("https://media.example.com/cove/api/extensions/com.alextomas955.whisparrsync/callback")]
    [InlineData("not-a-url")]
    public void TheMergeIsAFixedPoint(string edited)
    {
        var once = CallbackAddress.HostPartOf(edited, ExtensionId);
        Assert.Equal(once, CallbackAddress.HostPartOf(once, ExtensionId));
    }

    [Theory]
    [InlineData("http://cove:5073")]
    [InlineData("https://media.example.com/cove")]
    public void BothFormsReadBackToTheHostTheyWereBuiltFrom(string host)
    {
        Assert.Equal(host, CallbackAddress.HostPartOf(
            CallbackAddress.WithoutSecret(host, ExtensionId), ExtensionId));
        Assert.Equal(host, CallbackAddress.HostPartOf(
            CallbackAddress.WithSecret(host, ExtensionId, "a-secret"), ExtensionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyOrWhitespaceStoredHostFallsBackToTheRequestHost(string? stored)
        => Assert.Equal("http://cove:5073", CallbackAddress.ResolveHost(stored, "http://cove:5073"));

    // Storing the host is what keeps a later request on a different host from moving the address.
    // A rule that skipped the store when stored and request host agreed would lose that.
    [Fact]
    public void AStoredHostIsUsedEvenWhenItEqualsTheHostItWasStoredFrom()
    {
        const string requestHost = "http://cove:5073";
        var stored = CallbackAddress.HostPartOf(requestHost, ExtensionId);

        Assert.Equal(requestHost, stored);
        Assert.Equal(requestHost, CallbackAddress.ResolveHost(stored, requestHost));
        Assert.Equal(requestHost, CallbackAddress.ResolveHost(stored, "http://someone-else:9999"));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("cove:5073")]
    [InlineData("ftp://cove:5073")]
    [InlineData("file:///etc/passwd")]
    public void AnAddressThatIsNotAnAbsoluteHttpUrlYieldsNoHost(string edited)
        => Assert.Equal("", CallbackAddress.HostPartOf(edited, ExtensionId));

    // Two forms exist because a query string is written to the access log of every proxy on the
    // delivery path, and a pasted address has nowhere else to carry the secret.
    [Fact]
    public void TheRegisteredFormCarriesNoSecretAndTheCopyableFormDoes()
    {
        const string secret = "row07-not-a-real-secret";

        var registered = CallbackAddress.WithoutSecret("http://cove:5073", ExtensionId);
        var copyable = CallbackAddress.WithSecret("http://cove:5073", ExtensionId, secret);

        Assert.DoesNotContain(secret, registered, StringComparison.Ordinal);
        Assert.DoesNotContain("?", registered, StringComparison.Ordinal);
        Assert.Contains(secret, copyable, StringComparison.Ordinal);
        Assert.StartsWith(registered + "?", copyable, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRouteTheAddressNamesIsTheRouteTheExtensionMounts()
        => Assert.Equal(
            "/api/extensions/com.alextomas955.whisparrsync/callback",
            CallbackAddress.RouteFor(ExtensionId));

    // Distinctness only rules out a constant; a counter would pass it too. The width is asserted
    // beside it because a random source is no better than the width it was asked for.
    [Fact]
    public void AMintedSecretCarriesTheFullWidthAndTwoMintsDiffer()
    {
        var minted = Enumerable.Range(0, 16).Select(_ => CallbackSecret.Mint()).ToList();

        Assert.Equal(minted.Count, minted.Distinct(StringComparer.Ordinal).Count());
        foreach (var secret in minted)
        {
            Assert.Equal(CallbackSecret.EntropyBytes, Base64Url.DecodeFromChars(secret).Length);
        }
    }

    [Fact]
    public void AMintedSecretMatchesItselfAndNothingElse()
    {
        var secret = CallbackSecret.Mint();

        Assert.True(CallbackSecret.Matches(secret, secret));
        Assert.False(CallbackSecret.Matches(secret, secret[..^1]));
        Assert.False(CallbackSecret.Matches(secret, secret + "x"));
        Assert.False(CallbackSecret.Matches(secret, CallbackSecret.Mint()));
        Assert.False(CallbackSecret.Matches(secret, null));
        Assert.False(CallbackSecret.Matches(secret, ""));
        Assert.False(CallbackSecret.Matches(null, secret));
        Assert.False(CallbackSecret.Matches("", ""));
    }

    // Timing is not measurable in a unit test, so this asserts the routine the compiled method
    // calls. The IL scan over-approximates the call set, so the presence assertion is weak on its
    // own. The absence of a string equality beside it is what makes a rewrite to == fail here.
    [Fact]
    public void TheSecretComparisonIsTheConstantTimeOneRatherThanAnEquality()
    {
        var callees = CalleeNames(
            typeof(CallbackSecret).GetMethod(nameof(CallbackSecret.Matches))!);

        Assert.Contains(
            $"{nameof(CryptographicOperations)}.{nameof(CryptographicOperations.FixedTimeEquals)}",
            callees);
        Assert.DoesNotContain("String.op_Equality", callees);
        Assert.DoesNotContain("String.Equals", callees);
    }

    // The two Whisparr generations carry the secret differently, a custom header on one and Basic
    // auth on the other. The callback route reads both without knowing which instance sent one.
    [Fact]
    public void TheSecretIsReadFromEveryPositionAndAnOutOfBandOneWins()
    {
        Assert.Null(CallbackSecret.PresentedIn(null, null, null));
        Assert.Null(CallbackSecret.PresentedIn("  ", "", ""));

        Assert.Equal(
            new PresentedCallbackSecret("in-the-address", CallbackSecretPosition.Address),
            CallbackSecret.PresentedIn(null, null, "in-the-address"));

        Assert.Equal(
            new PresentedCallbackSecret("in-a-header", CallbackSecretPosition.OutOfBand),
            CallbackSecret.PresentedIn("in-a-header", null, null));

        Assert.Equal(
            new PresentedCallbackSecret("in-basic-auth", CallbackSecretPosition.OutOfBand),
            CallbackSecret.PresentedIn(null, BasicAuth(CallbackSecret.BasicAuthUser, "in-basic-auth"), null));

        // An out-of-band secret wins, so a query string an intermediary appended never classifies
        // the delivery.
        Assert.Equal(
            new PresentedCallbackSecret("in-a-header", CallbackSecretPosition.OutOfBand),
            CallbackSecret.PresentedIn("in-a-header", null, "in-the-address"));
        Assert.Equal(
            new PresentedCallbackSecret("in-basic-auth", CallbackSecretPosition.OutOfBand),
            CallbackSecret.PresentedIn(
                null, BasicAuth(CallbackSecret.BasicAuthUser, "in-basic-auth"), "in-the-address"));
    }

    // The secret is the password half, split on the first colon, because a secret may contain a
    // colon and a user name may not.
    [Theory]
    [InlineData("Bearer something", null)]
    [InlineData("Basic not-base64!!", null)]
    [InlineData("Basic ", null)]
    [InlineData("Digest dXNlcjpzZWNyZXQ=", null)]
    public void AnAuthorizationHeaderThatCarriesNoBasicPasswordPresentsNothing(
        string authorization, string? expected)
    {
        var presented = CallbackSecret.PresentedIn(null, authorization, null);
        Assert.Equal(expected, presented?.Value);
    }

    [Theory]
    [InlineData("user", "secret", "secret")]
    [InlineData("user", "sec:ret", "sec:ret")]
    [InlineData("", "secret", "secret")]
    public void TheBasicAuthSecretIsThePasswordHalf(string user, string password, string expected)
        => Assert.Equal(
            expected,
            CallbackSecret.PresentedIn(null, BasicAuth(user, password), null)?.Value);

    [Theory]
    [InlineData("user", "")]
    [InlineData("no-colon-at-all", null)]
    public void ABasicCredentialWithNoPasswordPresentsNothing(string user, string? password)
    {
        var credential = password is null ? user : $"{user}:{password}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credential));

        Assert.Null(CallbackSecret.PresentedIn(null, $"Basic {encoded}", null));
    }

    private static string BasicAuth(string user, string password)
        => "Basic " + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));

    // Every method token in the IL, as Type.Member.
    private static HashSet<string> CalleeNames(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException(
                $"{method.Name} has no IL body, so nothing can be said about what it calls.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset + sizeof(int) <= il.Length; offset++)
        {
            var token = BitConverter.ToInt32(il, offset);
            MethodBase? callee;
            try
            {
                callee = method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method.GetGenericArguments());
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (callee?.DeclaringType is { } declaring)
            {
                names.Add($"{declaring.Name}.{callee.Name}");
            }
        }

        return names;
    }
}
