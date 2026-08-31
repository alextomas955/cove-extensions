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

/// <summary>
/// The API key is write-only because nothing on the way out has anywhere to put it.
/// </summary>
/// <remarks>
/// The type-level assertions are the ones worth having: a search of a rendered response can only fail
/// on a key that happens to be in the sample, while a type with no member of the right shape cannot
/// carry one whatever it is filled with.
/// </remarks>
public sealed partial class SettingsProjectionTests
{
    /// <summary>The words a member carrying a secret would be named with.</summary>
    private static readonly string[] CredentialVocabulary =
        ["key", "secret", "token", "password", "credential", "auth"];

    /// <summary>
    /// Every string the settings view can carry, by declaring type and member name.
    /// </summary>
    /// <remarks>
    /// Transcribed by hand. A member added to the view fails this until it is named here, which is
    /// what makes the list a decision rather than a description.
    /// </remarks>
    private static readonly string[] StringsTheViewMayCarry =
    [
        "WhisparrSyncGenerationSettingsView.Address",
        "WhisparrSyncGenerationSettingsView.RecordedVersion",
    ];

    /// <summary>The settings the host serializes an extension's responses with.</summary>
    private static readonly JsonSerializerOptions HostJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TheSettingsViewHasNoMemberThatCouldCarryAKey()
    {
        var strings = StringMembersOf(typeof(WhisparrSyncSettingsView)).ToList();

        Assert.NotEmpty(strings);
        Assert.DoesNotContain(
            strings,
            member => CredentialVocabulary.Any(
                word => member.Contains(word, StringComparison.OrdinalIgnoreCase)));
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

        await global::WhisparrSync.WhisparrSync.SaveSettingsAsync(
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest("http://whisparr-v3:6969", KeyWriteSignal.Replace, key),
                null),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure),
            options,
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

        // The discriminating control: the response DID describe the connection the key belongs to, so
        // its silence about the key is about the key rather than about an empty answer.
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

    /// <summary>
    /// A key would arrive at a log line as a string, so the strings the templates take are pinned.
    /// </summary>
    /// <remarks>
    /// Transcribed by hand. A template that grows a string parameter fails this until the parameter is
    /// named here, which is the point at which someone decides whether it can carry a key.
    /// </remarks>
    /// <remarks>
    /// <c>ImportEventTypeIgnored.eventType</c> is the one entry here whose value an outside caller
    /// chooses. It is admitted because the alternative is a line that says an unrecognised event
    /// arrived without saying which, and because the handler shortens it before it is passed: a
    /// credential presented to that route travels in a header or the address, never in the event
    /// type, and a body long enough to hide one in is refused before it is parsed.
    /// <para>
    /// Both <c>host</c> entries are host names, which is the most an outbound failure is given.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLogTemplatesTakeOnlyTheStringsTheyAreMeantTo()
        => Assert.Equal(
            new[]
            {
                "ConnectionTransportFailure.host",
                "ImportEventTypeIgnored.eventType",
                "ReportedRootReadFailed.host",
            }.Order(),
            LogTemplates()
                .SelectMany(template => template.GetParameters(), (template, parameter) => (template, parameter))
                .Where(pair => pair.parameter.ParameterType == typeof(string))
                .Select(pair => $"{pair.template.Name}.{pair.parameter.Name}")
                .Order());

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

    /// <summary>Every source-generated log method this extension declares.</summary>
    private static IEnumerable<MethodInfo> LogTemplates()
        => typeof(global::WhisparrSync.WhisparrSync).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<LoggerMessageAttribute>() is not null);

    private static string MessageOf(MethodInfo template)
        => template.GetCustomAttribute<LoggerMessageAttribute>()?.Message ?? "";

    /// <summary>
    /// Every string a type can carry, by declaring type and member name, walking the whole graph.
    /// </summary>
    /// <remarks>
    /// A member of a type this walk cannot read fails outright rather than being skipped: a member it
    /// cannot read is a member it cannot say is free of a key.
    /// </remarks>
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

    [GeneratedRegex(@"\{(\w+)")]
    private static partial Regex Placeholder();
}
