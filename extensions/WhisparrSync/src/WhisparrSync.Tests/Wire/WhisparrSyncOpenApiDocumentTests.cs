using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
// Two types in this assembly's reach are named MonitorScope: the stored settings default carries the
// spec's earlier vocabulary, and the acting one carries the two names both generations use. Only the
// acting one reaches the wire.
using MonitorScope = WhisparrSync.Monitoring.MonitorScope;
using WhisparrEntityKind = WhisparrSync.Monitoring.WhisparrEntityKind;

namespace WhisparrSync.Tests.Wire;

/// <summary>
/// Emits Whisparr Sync's wire document from its shipped registration and fails when it differs from
/// the committed copy. The endpoints are mounted in a real in-process host, though the emit sends no
/// request.
/// </summary>
/// <remarks>
/// The facts below are read from the committed document rather than from the C# source, and the
/// inherited emit is what makes that sound: it fails when the committed copy differs from what the
/// registrations produce, so a hand edit cannot satisfy these and a drift cannot hide behind them.
/// </remarks>
public sealed class WhisparrSyncOpenApiDocumentTests : ExtensionOpenApiDocumentTests
{
    /// <summary>
    /// The tag every route states for itself.
    /// </summary>
    /// <remarks>
    /// Transcribed rather than read from the registration, which declares it privately. An
    /// unstated tag is INFERRED from the entry assembly, which during an emit is the test runner, so
    /// a route that stopped stating one would move the committed document the day that runner
    /// changes. Comparing against this name is what reports that.
    /// </remarks>
    private const string WireTag = "WhisparrSync";

    /// <summary>The enums whose wire spelling this extension serves.</summary>
    private static readonly Type[] WireEnums =
    [
        typeof(MonitorScope),
        typeof(WhisparrEntityKind),
        typeof(MonitorRefusalKind),
        typeof(WhisparrCapability),
    ];

    protected override IApiExtension CreateExtension() => WhisparrSyncFixture.Create();

    protected override void ConfigureBindingServices(IServiceCollection services)
        => services.AddWhisparrSyncBindingServices();

    [Fact]
    public void EveryResponsePropertyIsCamelCase()
    {
        using var document = CommittedDocument();
        var offenders = new List<string>();
        var names = 0;

        foreach (var schema in Schemas(document))
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                names++;
                if (!IsCamelCase(property.Name))
                {
                    offenders.Add($"{schema.Name}.{property.Name}");
                }
            }
        }

        // A document that described no property at all would otherwise satisfy the comparison below
        // by having nothing to disagree about.
        Assert.NotEqual(0, names);
        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryEnumValueIsCamelCase()
    {
        using var document = CommittedDocument();
        var offenders = new List<string>();
        var values = 0;

        foreach (var schema in Schemas(document))
        {
            if (!schema.Value.TryGetProperty("enum", out var members))
            {
                continue;
            }

            foreach (var member in members.EnumerateArray())
            {
                // A nullable enum carries a null beside its names, which has no spelling to check.
                if (member.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                values++;
                var name = member.GetString()!;
                if (!IsCamelCase(name))
                {
                    offenders.Add($"{schema.Name}.{name}");
                }
            }
        }

        Assert.NotEqual(0, values);
        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryOperationStatesTheWireTag()
    {
        using var document = CommittedDocument();
        var untagged = new List<string>();
        var operations = 0;

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                operations++;
                var stated = method.Value.TryGetProperty("tags", out var tags)
                    && tags.EnumerateArray().Any(tag => tag.GetString() == WireTag);

                if (!stated)
                {
                    untagged.Add($"{method.Name} {path.Name}");
                }
            }
        }

        Assert.NotEqual(0, operations);
        Assert.Empty(untagged);
    }

    [Fact]
    public void EveryWireEnumDeclaresItsSpellingOnItsOwnType()
    {
        foreach (var type in WireEnums)
        {
            var declared = type.GetCustomAttribute<JsonConverterAttribute>();

            Assert.True(
                declared is not null,
                $"{type.Name} declares no wire spelling on its own type, so its values are whatever "
                    + "the serializer in reach happens to write.");

            Assert.Equal(typeof(CamelCaseStringEnumConverter), declared!.ConverterType);
        }
    }

    [Fact]
    public void NoSerializerOptionsCollectionRegistersAConverter()
    {
        var sources = Directory
            .EnumerateFiles(Path.Combine(ExtensionRoot(), "src", "WhisparrSync"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .ToList();

        // A scan that reached no source would report nothing wrong for the same reason it reported
        // nothing at all.
        Assert.NotEmpty(sources);

        // An equivalent converter in an options collection OUTRANKS the attribute on the type rather
        // than duplicating it, so a second declaration could drift and win in silence. No wire
        // document diff would reveal it: both spellings stay camelCase until one of them changes.
        var offenders = sources
            .Where(file => File.ReadAllText(file).Contains("Converters.Add", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToList();

        Assert.Empty(offenders);
    }

    // Takes the document rather than opening its own: a JsonElement is a view over the document that
    // produced it and reads as disposed once that document is.
    private static List<JsonProperty> Schemas(JsonDocument document)
        => document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .EnumerateObject()
            .ToList();

    private static JsonDocument CommittedDocument()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(ExtensionRoot(), "wire", "openapi.json")));

    // The wire spelling: a leading lower-case letter and nothing but letters and digits after it.
    // Tested directly rather than by round-tripping through a naming policy, because a name the
    // policy would mangle the same way would agree with itself and pass.
    private static bool IsCamelCase(string name)
        => name.Length > 0 && char.IsLower(name[0]) && name.All(char.IsLetterOrDigit);

    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // Found by the document this class is about rather than by a counted-out "..": the test
    // assembly's depth below the extension directory varies with configuration and target framework.
    private static string ExtensionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "wire", "openapi.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No wire/openapi.json above {AppContext.BaseDirectory}, so the committed wire document "
                + "cannot be read.");
    }
}
