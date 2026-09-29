using System.Reflection;
using System.Text.Json;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

// The API key is write-only: these cases fix that nothing on the way out has anywhere to put it.
public sealed class SettingsProjectionTests
{
    // The settings the host serializes an extension's responses with.
    private static readonly JsonSerializerOptions HostJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AStoredKeyReachesNeitherTheResponseNorTheStoredBlob()
    {
        const string key = "a4b8c1d5e9f20738a4b8c1d5e9f20738";
        var store = new FakeStore();
        var options = new OptionsStore(store);
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, "http://whisparr-v3:6969", key);

        // A reading the blob keeps, so what the blob does not hold is read against a blob that holds
        // something. Without it a save at this address stores nothing, and every absence would pass.
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                SelectedGeneration = WhisparrGeneration.V3,
                V3 = new WhisparrSyncGenerationConnection { RecordedVersion = "3.3.8.1097" },
            },
            TestContext.Current.CancellationToken);

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

        // The blob holds neither the key nor the address: both live in the credential row, which
        // the host's bulk extension-data route does not serve. The generation is the control, so
        // the blob's silence about the two is not an empty answer.
        var stored = await store.GetAllAsync(TestContext.Current.CancellationToken);
        var blob = string.Join('\n', stored.Values);
        Assert.Contains("3.3.8.1097", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("whisparr-v3:6969", blob, StringComparison.Ordinal);
        Assert.DoesNotContain(key, blob, StringComparison.OrdinalIgnoreCase);
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

    private static IEnumerable<MethodInfo> LogTemplates()
        => typeof(global::WhisparrSync.WhisparrSync).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<LoggerMessageAttribute>() is not null);
}
