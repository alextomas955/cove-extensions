using System.Reflection;
using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class OptionsStoreTests
{
    // Every reference member RenamerOptions gives a default. A member added later joins the theory
    // without an edit here, which is the point: the store restores a stored null by the declared
    // nullability, so a member declared nullable with a default would keep the null and fail here.
    public static TheoryData<string> MembersWithADefault()
    {
        var defaults = new RenamerOptions();
        var data = new TheoryData<string>();
        foreach (var property in typeof(RenamerOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.PropertyType.IsValueType && property.CanWrite && property.GetValue(defaults) is not null)
            {
                data.Add(property.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MembersWithADefault))]
    public async Task LoadAsync_MemberStoredAsNull_TakesItsDefault(string member)
    {
        var fake = new FakeStore();
        await fake.SetAsync(OptionsStore.Key, $$"""{"{{member}}":null}""");

        var loaded = await new OptionsStore(fake).LoadAsync();

        var property = typeof(RenamerOptions).GetProperty(member)!;
        Assert.Equal(
            JsonSerializer.Serialize(property.GetValue(new RenamerOptions()), RenamerOptions.JsonOptions),
            JsonSerializer.Serialize(property.GetValue(loaded), RenamerOptions.JsonOptions));
    }

    [Fact]
    public async Task LoadAsync_NonPositiveLengthCaps_FallBackToTheDefaults()
    {
        var fake = new FakeStore();
        await fake.SetAsync(OptionsStore.Key, """{"FilenameMax":-5,"FullPathMax":0}""");

        var loaded = await new OptionsStore(fake).LoadAsync();

        Assert.Equal(new RenamerOptions().FilenameMax, loaded.FilenameMax);
        Assert.Equal(new RenamerOptions().FullPathMax, loaded.FullPathMax);
    }

    [Fact]
    public async Task LoadAsync_SmallButPositiveLengthCap_IsKeptAsStored()
    {
        // A tight budget is a configuration, not a mistake: only a cap that cannot be a budget at all
        // is replaced, so a stored value a user chose is never quietly widened.
        var fake = new FakeStore();
        await fake.SetAsync(OptionsStore.Key, """{"FilenameMax":14,"FullPathMax":40}""");

        var loaded = await new OptionsStore(fake).LoadAsync();

        Assert.Equal(14, loaded.FilenameMax);
        Assert.Equal(40, loaded.FullPathMax);
    }
}
