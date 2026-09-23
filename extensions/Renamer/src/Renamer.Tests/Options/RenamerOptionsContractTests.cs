using System.Reflection;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class RenamerOptionsContractTests
{
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
    public async Task LoadAsync_MemberStoredAsNull_IsNotNull(string member)
    {
        var fake = new FakeStore();
        await fake.SetAsync(OptionsStore.Key, $$"""{"{{member}}":null}""");

        var loaded = await new OptionsStore(fake).LoadAsync();

        Assert.NotNull(typeof(RenamerOptions).GetProperty(member)!.GetValue(loaded));
    }
}
