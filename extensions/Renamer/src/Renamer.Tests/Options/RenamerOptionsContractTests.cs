using System.Reflection;
using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// The load contract every <see cref="RenamerOptions"/> member is held to: a stored explicit
/// <c>null</c> never reaches a member the model gives a default.
/// </summary>
/// <remarks>
/// The members are enumerated from the model rather than listed here, so one added later is covered
/// with no edit to this file. The criterion is a non-null value on a fresh instance, which is read
/// off the defaults and not off the rule the store applies — an expectation computed from the code
/// it checks would agree with that code however wrong both were.
/// </remarks>
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

    [Fact]
    public void MembersWithADefault_IsNotEmpty()
    {
        // Guards the theory itself: an enumeration that silently found nothing would pass every case.
        Assert.NotEmpty(MembersWithADefault());
    }
}
