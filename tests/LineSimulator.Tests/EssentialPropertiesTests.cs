using System.Reflection;
using LineSimulator.Core;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

public class EssentialPropertiesTests
{
    [Fact]
    public void EveryEssentialName_IsAnEditablePropertyOfItsType()
    {
        var types = typeof(LineProject).Assembly.GetTypes().ToDictionary(t => t.Name);
        foreach (var (typeName, names) in EssentialProperties.All)
        {
            Assert.True(types.ContainsKey(typeName), typeName);
            var shown = types[typeName].GetProperties().Where(p => p.GetCustomAttribute<UiPropertyAttribute>() != null).Select(p => p.Name).ToHashSet();
            foreach (var n in names) Assert.True(shown.Contains(n), $"{typeName}.{n}");
            Assert.True(shown.Count(n => !EssentialProperties.IsAdvanced(typeName, n)) <= 7, $"{typeName}: scheda breve troppo lunga");
        }
    }

    [Fact]
    public void SimpleMachines_ShowFewFields_AndOverrideIsAlwaysVisible()
    {
        Assert.True(EssentialProperties.IsAdvanced("StrapperConfig", "ConsentTimeoutS"));
        Assert.False(EssentialProperties.IsAdvanced("StrapperConfig", "PositionMm"));
        Assert.False(EssentialProperties.IsAdvanced("PhotocellConfig", "OverrideGroup"));
        Assert.False(EssentialProperties.IsAdvanced("UnknownType", "Anything"));
    }
}
