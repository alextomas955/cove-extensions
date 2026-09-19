using System.Collections;
using System.Reflection;
using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// The persistence contract every <see cref="RenamerOptions"/> member is held to: a value set on a
/// member survives the serializer the store writes and reads blobs with.
/// </summary>
/// <remarks>
/// The members are enumerated from the model rather than listed here, so one added later is covered
/// with no edit to this file. Each case seeds one member with a value the defaults do not already
/// hold, which is what makes a member that never reaches the blob fail rather than agree with the
/// default it was compared against.
/// <para>
/// A member of a nested options record is reached by its own path, and the comparison reads that
/// member back rather than the record holding it: a record compared whole passes when a member is
/// missing from the blob, because the same serializer drops it from both sides.
/// </para>
/// <para>
/// A record inside a list or a dictionary is compared whole, so this does not reach its members.
/// <c>KindOptions</c> and the destination maps are covered that way, and by the suites that assert
/// their stored shape directly.
/// </para>
/// </remarks>
public sealed class OptionsPersistenceTests
{
    public static TheoryData<string> PersistedMembers()
    {
        var data = new TheoryData<string>();
        foreach (var path in MemberPaths(typeof(RenamerOptions), prefix: ""))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PersistedMembers))]
    public void Member_SetToANonDefaultValue_SurvivesTheBlob(string path)
    {
        var segments = path.Split('.');

        var seeded = new RenamerOptions();
        var owner = Owner(seeded, segments);
        var member = owner.GetType().GetProperty(segments[^1])!;
        member.SetValue(owner, Distinct(member.PropertyType, member.GetValue(owner), depth: 0));

        // The seed has to differ from the defaults, or a member dropped by the serializer would come
        // back holding the value this case expects and the case would pass having proven nothing.
        Assert.NotEqual(Json(Read(new RenamerOptions(), segments)), Json(Read(seeded, segments)));

        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(
            JsonSerializer.Serialize(seeded, RenamerOptions.JsonOptions),
            RenamerOptions.JsonOptions);

        Assert.Equal(Json(Read(seeded, segments)), Json(Read(reloaded!, segments)));
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
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)];

    /// <summary>An options record reached through a property, as opposed to a scalar or a collection.</summary>
    private static bool IsNestedRecord(Type type)
        => type.IsClass
        && type != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(type)
        && type.Assembly == typeof(RenamerOptions).Assembly;

    /// <summary>The object holding the last segment, creating an absent record on the way.</summary>
    private static object Owner(RenamerOptions root, string[] segments)
    {
        object current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var property = current.GetType().GetProperty(segments[i])!;
            var next = property.GetValue(current);
            if (next is null)
            {
                next = Activator.CreateInstance(property.PropertyType)!;
                property.SetValue(current, next);
            }

            current = next;
        }

        return current;
    }

    private static object? Read(RenamerOptions root, string[] segments)
    {
        object? current = root;
        foreach (var segment in segments)
        {
            if (current is null)
            {
                return null;
            }

            current = current.GetType().GetProperty(segment)!.GetValue(current);
        }

        return current;
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value, RenamerOptions.JsonOptions);

    /// <summary>Builds a value of <paramref name="type"/> that <paramref name="current"/> does not already hold.</summary>
    private static object? Distinct(Type type, object? current, int depth)
    {
        if (depth > 4)
        {
            return current;
        }

        if (type == typeof(bool))
        {
            return !(bool)(current ?? false);
        }

        if (type == typeof(string))
        {
            return current as string == "probe" ? "probe-other" : "probe";
        }

        if (type == typeof(int))
        {
            return (int)(current ?? 0) + 4242;
        }

        if (type == typeof(long))
        {
            return (long)(current ?? 0L) + 4242L;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).Cast<object>().First(v => !Equals(v, current));
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            list.Add(Distinct(type.GetGenericArguments()[0], null, depth + 1));
            return list;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = type.GetGenericArguments();
            var map = (IDictionary)Activator.CreateInstance(type)!;
            map[Distinct(args[0], null, depth + 1)!] = Distinct(args[1], null, depth + 1);
            return map;
        }

        var nested = Activator.CreateInstance(type)!;
        foreach (var property in Writable(type))
        {
            property.SetValue(nested, Distinct(property.PropertyType, property.GetValue(nested), depth + 1));
        }

        return nested;
    }
}
