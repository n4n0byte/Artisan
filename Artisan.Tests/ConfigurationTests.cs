using System.Reflection;
using System.Runtime.CompilerServices;
using Artisan;
using Xunit;

namespace Artisan.Tests;

public class ConfigurationTests
{
    [Fact]
    public void CraftOptimizationIsDisabledByDefault()
    {
        var setting = typeof(Configuration).GetField("EnableCraftOptimization", BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(setting);
        Assert.Equal(typeof(bool), setting.FieldType);
        var config = RuntimeHelpers.GetUninitializedObject(typeof(Configuration));
        Assert.False((bool)setting.GetValue(config)!);
    }
}
