using Novee2Mqtt.Core;

namespace Novee2Mqtt.Platform;

public sealed record TemperatureConstraints(TemperatureValue Min, TemperatureValue Max)
{
    public TemperatureConstraints As(TemperatureUnits units) => new(Min.As(units), Max.As(units));

    /// <summary>
    /// Reads the min/max of a <c>temperatureSetting</c> capability. The units of
    /// the range and the units the device expects for a set request can differ,
    /// so the range is converted into the capability's declared unit.
    /// </summary>
    public static TemperatureConstraints Parse(DeviceCapability capability)
    {
        var declaredUnits = TemperatureUnits.Fahrenheit;
        var unitDefault = capability.StructFieldByName("unit")?.DefaultValue.AsString();
        if (unitDefault is not null && TemperatureExtensions.TryParseScale(unitDefault, out var scale))
        {
            declaredUnits = scale.ToUnits();
        }

        var temperature = capability.StructFieldByName("temperature")
            ?? throw new GoveeException($"no temperature field in capability {capability.Instance}");

        if (temperature.FieldType is not IntegerParameters integer)
        {
            throw new GoveeException($"unexpected temperature value in capability {capability.Instance}");
        }

        var rangeUnits = declaredUnits;
        if (integer.Unit is not null && TemperatureExtensions.TryParseScale(integer.Unit, out var rangeScale))
        {
            rangeUnits = rangeScale.ToUnits();
        }

        var min = new TemperatureValue(integer.Range.Min, rangeUnits);
        var max = new TemperatureValue(integer.Range.Max, rangeUnits);

        return new TemperatureConstraints(min.As(declaredUnits), max.As(declaredUnits));
    }
}
