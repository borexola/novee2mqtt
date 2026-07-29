using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Hass;

namespace Novee2Mqtt.Tests;

public class TemperatureTests
{
    [Fact]
    public void ParsesBareNumberAsCelsiusByDefault()
    {
        Assert.Equal(new TemperatureValue(23.0, TemperatureUnits.Celsius), TemperatureValue.Parse("23"));
        Assert.Equal(new TemperatureValue(23.3, TemperatureUnits.Celsius), TemperatureValue.Parse("23.3"));
    }

    [Fact]
    public void ParsesExplicitScale()
    {
        Assert.Equal(new TemperatureValue(23.0, TemperatureUnits.Celsius), TemperatureValue.Parse("23C"));
        Assert.Equal(new TemperatureValue(23.0, TemperatureUnits.Celsius), TemperatureValue.Parse(" 23 C "));
    }

    [Fact]
    public void ExplicitScaleBeatsFallback()
    {
        Assert.Equal(
            new TemperatureValue(23.0, TemperatureUnits.Celsius),
            TemperatureValue.Parse("23C", TemperatureScale.Fahrenheit));
    }

    [Fact]
    public void FallbackAppliesWhenScaleIsAbsent()
    {
        Assert.Equal(
            new TemperatureValue(23.0, TemperatureUnits.Fahrenheit),
            TemperatureValue.Parse("23", TemperatureScale.Fahrenheit));
    }

    [Fact]
    public void RejectsUnknownScale()
    {
        var ex = Assert.Throws<GoveeException>(() => TemperatureValue.Parse("23frogs"));
        Assert.Equal("Unknown temperature scale frogs", ex.Message);
    }

    [Fact]
    public void FormatsNormalizedValue()
    {
        Assert.Equal("22°C", new TemperatureValue(22.0, TemperatureUnits.Celsius).ToString());
        Assert.Equal("22°C", new TemperatureValue(2200.0, TemperatureUnits.CelsiusTimes100).ToString());
    }

    [Fact]
    public void ConvertsBetweenScales()
    {
        Assert.Equal(24.0, Math.Floor(new TemperatureValue(76, TemperatureUnits.Fahrenheit).AsCelsius()));
        Assert.Equal(76.0, Math.Ceiling(new TemperatureValue(24.444, TemperatureUnits.Celsius).AsFahrenheit()));
    }

    [Fact]
    public void ConvertsToAndFromScaledUnits()
    {
        Assert.Equal(7600.0, new TemperatureValue(76, TemperatureUnits.Fahrenheit).As(TemperatureUnits.FahrenheitTimes100).Value);
        Assert.Equal(2400.0, new TemperatureValue(24, TemperatureUnits.Celsius).As(TemperatureUnits.CelsiusTimes100).Value);
        Assert.Equal(24.0, new TemperatureValue(2400, TemperatureUnits.CelsiusTimes100).As(TemperatureUnits.Celsius).Value);
    }

    [Fact]
    public void ScaledUnitsHaveNoHomeAssistantUnit()
    {
        Assert.Null(TemperatureUnits.CelsiusTimes100.UnitOfMeasurement());
        Assert.Equal("°C", TemperatureUnits.Celsius.UnitOfMeasurement());
    }
}

public class DeviceNameTests
{
    [Theory]
    [InlineData("H6000", "AA:BB:CC:DD:EE:FF:42:2A", "H6000_422A")]
    [InlineData("H6127", "cef142b0b354995f", "H6127_995F")]
    [InlineData("H6127", "ce", "H6127_CE")]
    public void ComputesNameFromSkuAndIdSuffix(string sku, string id, string expected)
    {
        Assert.Equal(expected, new Device(sku, id).Name());
    }
}

public class TopicTests
{
    [Theory]
    [InlineData("powerSwitch", "Power Switch")]
    [InlineData("oscillationToggle", "Oscillation Toggle")]
    public void SplitsCamelCase(string input, string expected)
    {
        Assert.Equal(expected, Topics.CamelCaseToSpaceSeparated(input));
    }

    [Fact]
    public void TopicSafeStringLowercasesAndReplacesSeparators()
    {
        Assert.Equal("one_two_three", Topics.TopicSafeString("One:Two Three"));
    }

    [Fact]
    public void TopicSafeIdStripsSeparatorsButKeepsCase()
    {
        Assert.Equal("AABBCCDD", Topics.TopicSafeId(new Device("H6000", "AA:BB:CC:DD")));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2000, 500)]
    [InlineData(6535, 153)]
    public void ConvertsKelvinToMireds(int kelvin, int mireds)
    {
        Assert.Equal(mireds, Topics.KelvinToMired(kelvin));
    }

    [Fact]
    public void MiredConversionRoundTripsApproximately()
    {
        var mireds = Topics.KelvinToMired(4000);
        Assert.InRange(Topics.MiredToKelvin(mireds), 3990, 4010);
    }
}

public class CssColorTests
{
    [Theory]
    [InlineData("#ff0000", 255, 0, 0)]
    [InlineData("#0f0", 0, 255, 0)]
    [InlineData("00ff00", 0, 255, 0)]
    [InlineData("rgb(1, 2, 3)", 1, 2, 3)]
    [InlineData("rgba(1, 2, 3, 0.5)", 1, 2, 3)]
    [InlineData("red", 255, 0, 0)]
    [InlineData("RebeccaPurple", 0x66, 0x33, 0x99)]
    public void ParsesColorSyntaxes(string input, byte r, byte g, byte b)
    {
        Assert.True(CssColor.TryParse(input, out var color));
        Assert.Equal(new DeviceColor(r, g, b), color);
    }

    [Fact]
    public void RejectsNonsense()
    {
        Assert.False(CssColor.TryParse("not-a-color", out _));
    }

    [Fact]
    public void PacksAndUnpacksTheGoveeWireFormat()
    {
        var color = new DeviceColor(0x12, 0x34, 0x56);
        Assert.Equal(0x123456u, color.ToPacked());
        Assert.Equal(color, DeviceColor.FromPacked(0x123456));
    }
}

public class UuidV5Tests
{
    /// <summary>Well-known RFC 4122 test vector, so the client id matches the Govee app's.</summary>
    [Fact]
    public void MatchesTheReferenceVector()
    {
        Assert.Equal(
            new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"),
            UuidV5.Create(UuidV5.NamespaceDns, "python.org"));
    }

    [Fact]
    public void SimpleFormatHasNoHyphens()
    {
        var simple = UuidV5.CreateSimple(UuidV5.NamespaceDns, "python.org");
        Assert.Equal("886313e13b8a53729b900c9aee199e5d", simple);
    }
}

public class SceneUtilsTests
{
    [Fact]
    public void SortsCaseInsensitivelyAndDropsDuplicates()
    {
        var result = SceneUtils.SortAndDedup(["beta", "Alpha", "beta", "alpha"]);

        Assert.Equal(["Alpha", "alpha", "beta"], result);
    }
}

public class EnvTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("No", false)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    public void ParsesTruthyValues(string input, bool expected)
    {
        Assert.Equal(expected, Env.ParseTruthy(input));
    }

    [Fact]
    public void RejectsOtherValues()
    {
        Assert.Throws<GoveeException>(() => Env.ParseTruthy("maybe"));
    }
}
