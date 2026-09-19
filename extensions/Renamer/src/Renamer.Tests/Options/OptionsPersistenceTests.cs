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
/// </remarks>
public sealed class OptionsPersistenceTests
{
    public static TheoryData<string> PersistedMembers()
    {
        var data = new TheoryData<string>();
        foreach (var property in Members())
        {
            data.Add(property.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PersistedMembers))]
    public void Member_SetToANonDefaultValue_SurvivesTheBlob(string member)
    {
        var property = Members().Single(p => p.Name == member);
        var seeded = new RenamerOptions();
        var value = Distinct(property.PropertyType, property.GetValue(seeded), depth: 0);
        property.SetValue(seeded, value);

        // The seed has to differ from the defaults, or a member dropped by the serializer would come
        // back holding the value this case expects and the case would pass having proven nothing.
        Assert.NotEqual(Json(property.GetValue(new RenamerOptions())), Json(property.GetValue(seeded)));

        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(
            JsonSerializer.Serialize(seeded, RenamerOptions.JsonOptions),
            RenamerOptions.JsonOptions);

        Assert.Equal(Json(property.GetValue(seeded)), Json(property.GetValue(reloaded!)));
    }

    private static PropertyInfo[] Members()
        => [.. typeof(RenamerOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)];

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

        // A nested options record: a fresh instance with every member of its own made distinct.
        var nested = Activator.CreateInstance(type)!;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(nested, Distinct(property.PropertyType, property.GetValue(nested), depth + 1));
            }
        }

        return nested;
    }
}
