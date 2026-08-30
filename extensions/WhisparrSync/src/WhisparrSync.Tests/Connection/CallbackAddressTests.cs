using System.Buffers.Text;
using System.Reflection;
using System.Security.Cryptography;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Connection;

/// <summary>
/// The callback address's merge rule, its two forms, and the secret the second of them carries.
/// </summary>
/// <remarks>
/// The merge is the part of an edited address a user is allowed to change. Reading it as scheme, host
/// and port only would make a Cove behind a reverse proxy on a subpath unable to produce a working
/// callback, and the failure presents as Whisparr's.
/// </remarks>
public sealed class CallbackAddressTests
{
    private const string ExtensionId = "com.alextomas955.whisparrsync";

    /// <summary>
    /// The id the merge is exercised against is the SHIPPED one.
    /// </summary>
    /// <remarks>
    /// Without this the rest of the file would agree with whatever literal it declares, including one
    /// that names no route this extension mounts.
    /// </remarks>
    [Fact]
    public void TheIdTheseTestsUseIsTheOneTheExtensionShips()
        => Assert.Equal(ExtensionId, WhisparrSyncFixture.Manifest.Id);

    [Theory]
    // Scheme, host and port survive; the extension's own route and any secret are removed.
    [InlineData("http://cove:5073", "http://cove:5073")]
    [InlineData("https://media.example.com", "https://media.example.com")]
    [InlineData("http://cove:5073/", "http://cove:5073")]
    // The path prefix is the fourth part, which a scheme-host-port reading would drop.
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
    // A port that is the scheme's default reduces to one spelling, so a saved value stops depending
    // on how it was typed.
    [InlineData("http://cove:80/cove", "http://cove/cove")]
    public void TheMergeTakesSchemeHostPortAndPathPrefix(string edited, string expected)
        => Assert.Equal(expected, CallbackAddress.HostPartOf(edited, ExtensionId));

    /// <summary>
    /// Applying the merge to its own output returns the same value, so a saved-then-reloaded address
    /// does not drift.
    /// </summary>
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

    /// <summary>Both address forms read back to the host they were built from.</summary>
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

    /// <summary>
    /// A stored host equal to the request host is still the stored one.
    /// </summary>
    /// <remarks>
    /// What storing it buys is exactly this: a later request arriving on a different host does not
    /// move the address. A rule that skipped the store when the two agreed would lose that.
    /// </remarks>
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

    /// <summary>
    /// The registered form carries no secret and the copyable one does.
    /// </summary>
    /// <remarks>
    /// The difference is the whole point of having two forms: a query string is written to the access
    /// log of every proxy on the delivery path, and a pasted address has nowhere else to put one.
    /// </remarks>
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

    /// <summary>
    /// A minted secret is 256 bits of it, and two mints differ.
    /// </summary>
    /// <remarks>
    /// Distinctness is not randomness — it would hold for a counter too. What it rules out is a
    /// constant, and the width is asserted beside it because a cryptographic source drawn one byte at
    /// a time is no better than the width it was asked for.
    /// </remarks>
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

    /// <summary>
    /// The comparison is the constant-time one, and not an equality.
    /// </summary>
    /// <remarks>
    /// Timing is not measurable in a unit test, so what is asserted is the routine the compiled method
    /// calls. The IL is scanned for metadata tokens that resolve to a method, which OVER-approximates
    /// the call set — so the presence assertion is weak on its own and the absence of a string equality
    /// beside it is what makes a rewrite to <c>==</c> fail here.
    /// </remarks>
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

    [Fact]
    public void TheSecretIsReadFromEitherPositionAndOutOfBandWins()
    {
        Assert.Null(CallbackSecret.PresentedIn(null, null));
        Assert.Null(CallbackSecret.PresentedIn("  ", ""));

        var fromAddress = CallbackSecret.PresentedIn(null, "in-the-address");
        Assert.Equal(new PresentedCallbackSecret("in-the-address", CallbackSecretPosition.Address), fromAddress);

        var fromHeader = CallbackSecret.PresentedIn("in-a-header", null);
        Assert.Equal(new PresentedCallbackSecret("in-a-header", CallbackSecretPosition.OutOfBand), fromHeader);

        // A delivery from a registration this product made is never classified by a query string an
        // intermediary could have appended.
        var both = CallbackSecret.PresentedIn("in-a-header", "in-the-address");
        Assert.Equal(new PresentedCallbackSecret("in-a-header", CallbackSecretPosition.OutOfBand), both);
    }

    /// <summary>Every method token in <paramref name="method"/>'s IL, as <c>Type.Member</c>.</summary>
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
