using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

// The API key is write-only: these cases fix that nothing on the way out has anywhere to put it.
public sealed partial class SettingsProjectionTests
{
    private static readonly string[] CredentialVocabulary =
        ["key", "secret", "token", "password", "credential", "auth"];

    // Transcribed by hand, so a string member added to the view fails until it is named here.
    private static readonly string[] StringsTheViewMayCarry =
    [
        "WhisparrSyncGenerationSettingsView.Address",
        "WhisparrSyncGenerationSettingsView.RecordedVersion",
    ];

    // Members of the stored options record whose name reads like a credential, none of which
    // carries one. LastCallbackSecretPosition is an enum naming where an inbound delivery carried
    // its secret; the secret and the API key live in a table this record has no member for.
    private static readonly string[] OptionsMembersNamedLikeACredential =
    [
        "WhisparrSyncGenerationConnection.LastCallbackSecretPosition",
    ];

    // The settings the host serializes an extension's responses with.
    private static readonly JsonSerializerOptions HostJsonOptions = new(JsonSerializerDefaults.Web);

    // The bulk extension-data route serves this record whole, and it answers an unauthenticated
    // in-network caller on a Cove booted with authentication off. A search of a serialized sample
    // could only fail on a credential the sample happened to hold, so members are read off the type.
    [Fact]
    public void TheStoredOptionsRecordGrowsNoMemberNamedLikeACredential()
    {
        var names = MemberNamesOf(typeof(WhisparrSyncOptions), []).Distinct().ToList();

        Assert.NotEmpty(names);
        Assert.Equal(
            OptionsMembersNamedLikeACredential.Order(),
            names
                .Where(name => CredentialVocabulary.Any(
                    word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Order());
    }

    [Fact]
    public void TheSettingsViewCarriesOnlyTheStringsItIsMeantTo()
        => Assert.Equal(
            StringsTheViewMayCarry.Order(),
            StringMembersOf(typeof(WhisparrSyncSettingsView)).Distinct().Order());

    [Fact]
    public async Task AStoredKeyReachesNeitherTheResponseNorTheStoredBlob()
    {
        const string key = "a4b8c1d5e9f20738a4b8c1d5e9f20738";
        var store = new FakeStore();
        var options = new OptionsStore(store);
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, key);

        await WhisparrSyncFixture.Create().SaveSettingsAsync(
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest("http://whisparr-v3:6969", KeyWriteSignal.Replace, key),
                null),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure),
            options,
            new OptionsWriteGate(),
            credentials,
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        var read = await global::WhisparrSync.WhisparrSync.ReadSettingsAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure),
            options,
            credentials,
            TestContext.Current.CancellationToken);

        var body = JsonSerializer.Serialize(
            Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(read)).Value, HostJsonOptions);

        // The control: the response does describe the connection the key belongs to, so its silence
        // about the key is not just an empty answer.
        Assert.Contains("http://whisparr-v3:6969", body, StringComparison.Ordinal);
        Assert.Contains("\"keyIsSet\":true", body, StringComparison.Ordinal);
        Assert.DoesNotContain(key, body, StringComparison.OrdinalIgnoreCase);

        var stored = await store.GetAllAsync(TestContext.Current.CancellationToken);
        var blob = string.Join('\n', stored.Values);
        Assert.Contains("whisparr-v3:6969", blob, StringComparison.Ordinal);
        Assert.DoesNotContain(key, blob, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoLogTemplateTakesAParameterThatCouldCarryTheKey()
    {
        var templates = LogTemplates().ToList();
        Assert.NotEmpty(templates);

        var named = templates
            .SelectMany(template => template.GetParameters(), (template, parameter)
                => $"{template.Name}.{parameter.Name}")
            .Where(name => CredentialVocabulary.Any(
                word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            named.Count == 0,
            "these log parameters are named as though they carry a credential: " + string.Join(", ", named));
    }

    // The source generator hands an Exception parameter to the sink rather than rendering it, and a
    // sink writes it whole, message and path included.
    [Fact]
    public void NoContainedFailureTemplateTakesAnException()
    {
        var templates = LogTemplates()
            .Where(template => template.Name.EndsWith("Contained", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(templates);
        Assert.Empty(templates
            .SelectMany(template => template.GetParameters(), (template, parameter) => (template, parameter))
            .Where(pair => typeof(Exception).IsAssignableFrom(pair.parameter.ParameterType))
            .Select(pair => $"{pair.template.Name}.{pair.parameter.Name}")
            .ToList());
    }

    [Fact]
    public void NoLogMessageNamesACredentialPlaceholder()
    {
        var placeholders = LogTemplates()
            .SelectMany(template => Placeholder().Matches(MessageOf(template)).Select(match => match.Groups[1].Value))
            .Where(name => CredentialVocabulary.Any(
                word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(placeholders);
    }

    private static IEnumerable<MethodInfo> LogTemplates()
        => typeof(global::WhisparrSync.WhisparrSync).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<LoggerMessageAttribute>() is not null);

    private static string MessageOf(MethodInfo template)
        => template.GetCustomAttribute<LoggerMessageAttribute>()?.Message ?? "";

    // A member of a type this walk cannot read fails outright rather than being skipped: a member
    // it cannot read is a member it cannot say is free of a key.
    private static IEnumerable<string> StringMembersOf(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var memberType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (memberType == typeof(string))
            {
                yield return $"{type.Name}.{property.Name}";
            }
            else if (memberType.IsEnum
                || memberType == typeof(bool)
                || memberType == typeof(DateTimeOffset))
            {
                // A scalar with nowhere to hide a string.
            }
            else if (memberType.Namespace == typeof(WhisparrSyncSettingsView).Namespace)
            {
                foreach (var nested in StringMembersOf(memberType))
                {
                    yield return nested;
                }
            }
            else
            {
                Assert.Fail(
                    $"{type.Name}.{property.Name} is a {memberType.Name}, which this walk cannot read. "
                        + "A member it cannot read is a member it cannot say is free of a key.");
            }
        }
    }

    // Reads names rather than types, so a credential parked in a number, a collection or a nested
    // record is still named here.
    private static IEnumerable<string> MemberNamesOf(Type type, HashSet<Type> seen)
    {
        if (!seen.Add(type))
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return $"{type.Name}.{property.Name}";

            foreach (var nested in OwnTypesWithin(property.PropertyType)
                .SelectMany(owned => MemberNamesOf(owned, seen)))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<Type> OwnTypesWithin(Type type)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare.IsGenericType)
        {
            return bare.GetGenericArguments().SelectMany(OwnTypesWithin);
        }

        return bare.Assembly == typeof(global::WhisparrSync.WhisparrSync).Assembly && !bare.IsEnum
            ? [bare]
            : [];
    }

    [GeneratedRegex(@"\{(\w+)")]
    private static partial Regex Placeholder();
}
