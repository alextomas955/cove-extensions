using System.Collections;
using System.Reflection;
using System.Text.Json;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

// Every member the model declares, enumerated by path, so a setting added later is covered rather
// than uncovered. Members are read back one at a time: comparing the record that holds one would
// put both sides of the comparison through the serializer under test, and a member it dropped would
// be absent from the expected value too.
//
// A record inside a list or a dictionary is compared whole here.
// CollectionElementPersistenceTests reads those members back individually.
public sealed class OptionsPersistenceTests
{
    public static TheoryData<string> PersistedMembers()
    {
        var data = new TheoryData<string>();
        foreach (var path in MemberPaths(typeof(WhisparrSyncOptions), prefix: ""))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PersistedMembers))]
    public void MemberSetToANonDefaultValueSurvivesTheBlob(string path)
    {
        var segments = path.Split('.');
        var seeded = new WhisparrSyncOptions();
        var owner = Owner(seeded, segments);
        var member = owner.GetType().GetProperty(segments[^1])!;
        member.SetValue(owner, Distinct(member.PropertyType, member.GetValue(owner)));

        // The seed has to differ from the defaults, or a member the serializer dropped would come
        // back holding the value this case expects and the case would pass having proven nothing.
        Assert.NotEqual(Json(Read(new WhisparrSyncOptions(), segments)), Json(Read(seeded, segments)));

        var reloaded = JsonSerializer.Deserialize<WhisparrSyncOptions>(
            WhisparrSyncOptions.Persisted(seeded), WhisparrSyncOptions.JsonOptions);

        Assert.Equal(Json(Read(seeded, segments)), Json(Read(reloaded!, segments)));
    }

    // The gate writes nothing where the fold answered what it was given, so a member absent from
    // the comparison would make every write through it vanish with nothing reported.
    [Theory]
    [MemberData(nameof(PersistedMembers))]
    public void AFoldTouchingOnlyThisMemberIsSeenAsAChange(string path)
    {
        var segments = path.Split('.');
        var seeded = new WhisparrSyncOptions();
        var owner = Owner(seeded, segments);
        var member = owner.GetType().GetProperty(segments[^1])!;
        member.SetValue(owner, Distinct(member.PropertyType, member.GetValue(owner)));

        Assert.NotEqual(
            WhisparrSyncOptions.Persisted(new WhisparrSyncOptions()),
            WhisparrSyncOptions.Persisted(seeded));
    }

    private static IEnumerable<string> MemberPaths(Type type, string prefix)
    {
        foreach (var property in Writable(type))
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (IsNestedRecord(property.PropertyType))
            {
                foreach (var nested in MemberPaths(property.PropertyType, path))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    private static PropertyInfo[] Writable(Type type)
        => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.GetIndexParameters().Length == 0)];

    // An options record reached through a property, as opposed to a scalar or a collection.
    private static bool IsNestedRecord(Type type)
        => type.IsClass
            && type != typeof(string)
            && !typeof(IEnumerable).IsAssignableFrom(type)
            && type.Assembly == typeof(WhisparrSyncOptions).Assembly;

    // The record the last segment names a member of, created where the path runs through a slot the
    // defaults leave null.
    private static object Owner(WhisparrSyncOptions options, string[] segments)
    {
        object owner = options;
        foreach (var segment in segments[..^1])
        {
            var property = owner.GetType().GetProperty(segment)!;
            var held = property.GetValue(owner) ?? Activator.CreateInstance(property.PropertyType)!;
            property.SetValue(owner, held);
            owner = held;
        }

        return owner;
    }

    private static object? Read(WhisparrSyncOptions options, string[] segments)
    {
        object? at = options;
        foreach (var segment in segments)
        {
            at = at?.GetType().GetProperty(segment)?.GetValue(at);
        }

        return at;
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    // A value the defaults do not already hold, per member type.
    private static object? Distinct(Type type, object? current)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare == typeof(string))
        {
            return (string?)current == "seeded" ? "seeded-again" : "seeded";
        }

        if (bare == typeof(int))
        {
            return (current as int?) == 4242 ? 4343 : 4242;
        }

        if (bare == typeof(bool))
        {
            return !(current as bool? ?? false);
        }

        if (bare == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        }

        if (bare.IsEnum)
        {
            return Enum.GetValues(bare)
                .Cast<object>()
                .First(value => !Equals(value, current));
        }

        if (typeof(IEnumerable).IsAssignableFrom(bare) && bare.IsGenericType)
        {
            var element = bare.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            list.Add(Element(element));
            return list;
        }

        if (bare.IsClass)
        {
            return Seeded(Activator.CreateInstance(bare)!);
        }

        throw new InvalidOperationException(
            $"{bare} is a member type this theory has no distinct value for. Name one beside the "
                + "others rather than leaving the member uncovered.");
    }

    // One element for a list member, with every scalar it declares moved off its default so the
    // element read back is comparable.
    private static object Element(Type type)
        => type == typeof(string) ? "seeded" : Seeded(Activator.CreateInstance(type)!);

    private static object Seeded(object value)
    {
        foreach (var property in Writable(value.GetType()))
        {
            var bare = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (bare == typeof(string) || bare == typeof(int) || bare == typeof(bool) || bare.IsEnum)
            {
                property.SetValue(value, Distinct(property.PropertyType, property.GetValue(value)));
            }
        }

        return value;
    }

    // A theory with no data passes, so the count is asserted beside it.
    [Fact]
    public void EveryMemberOfTheModelIsWalked()
    {
        var walked = PersistedMembers().Count;

        Assert.True(
            walked > 20,
            $"{walked} members were walked, which is fewer than the model declares. The walk "
                + "stopped short of a nested record.");
    }
}
