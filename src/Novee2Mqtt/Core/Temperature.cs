using System.Globalization;

namespace Novee2Mqtt.Core;

public enum TemperatureScale
{
    Celsius,
    Fahrenheit,
}

/// <summary>
/// Govee reports temperatures in whichever unit it feels like, sometimes
/// scaled by 100. The scaled variants exist so a reading can be carried around
/// without losing that detail until it is normalized.
/// </summary>
public enum TemperatureUnits
{
    Celsius,
    CelsiusTimes100,
    Fahrenheit,
    FahrenheitTimes100,
}

public static class TemperatureConstants
{
    public const string UnitCelsius = "°C";
    public const string UnitFahrenheit = "°F";
    public const string DeviceClassTemperature = "temperature";
}

public static class TemperatureExtensions
{
    public static double Factor(this TemperatureUnits units) => units switch
    {
        TemperatureUnits.CelsiusTimes100 or TemperatureUnits.FahrenheitTimes100 => 100.0,
        _ => 1.0,
    };

    public static TemperatureScale Scale(this TemperatureUnits units) => units switch
    {
        TemperatureUnits.Celsius or TemperatureUnits.CelsiusTimes100 => TemperatureScale.Celsius,
        _ => TemperatureScale.Fahrenheit,
    };

    public static TemperatureUnits ToUnits(this TemperatureScale scale) => scale switch
    {
        TemperatureScale.Celsius => TemperatureUnits.Celsius,
        _ => TemperatureUnits.Fahrenheit,
    };

    public static string UnitOfMeasurement(this TemperatureScale scale) => scale switch
    {
        TemperatureScale.Celsius => TemperatureConstants.UnitCelsius,
        _ => TemperatureConstants.UnitFahrenheit,
    };

    /// <summary>Null for the scaled units, which have no meaningful HA unit.</summary>
    public static string? UnitOfMeasurement(this TemperatureUnits units)
        => units.Factor() == 1.0 ? units.Scale().UnitOfMeasurement() : null;

    public static bool TryParseScale(string? text, out TemperatureScale scale)
    {
        scale = TemperatureScale.Celsius;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim())
        {
            case "c" or "C" or "°c" or "°C" or "Celsius" or "celsius":
                scale = TemperatureScale.Celsius;
                return true;
            case "f" or "F" or "°f" or "°F" or "Fahrenheit" or "fahrenheit":
                scale = TemperatureScale.Fahrenheit;
                return true;
            default:
                return false;
        }
    }

    public static TemperatureScale ParseScale(string text)
        => TryParseScale(text, out var scale) ? scale : throw new GoveeException($"Unknown temperature scale {text}");
}

public readonly record struct TemperatureValue(double Value, TemperatureUnits Units)
{
    public static double FahrenheitToCelsius(double f) => (f - 32.0) * (5.0 / 9.0);

    public static double CelsiusToFahrenheit(double c) => (c * 9.0 / 5.0) + 32.0;

    /// <summary>Strips any x100 scaling, keeping the same scale.</summary>
    public TemperatureValue Normalize() => new(Value / Units.Factor(), Units.Scale().ToUnits());

    public TemperatureValue As(TemperatureUnits target)
    {
        if (Units == target)
        {
            return this;
        }

        var normalized = Value / Units.Factor();
        var converted = (Units.Scale(), target.Scale()) switch
        {
            (TemperatureScale.Celsius, TemperatureScale.Fahrenheit) => CelsiusToFahrenheit(normalized),
            (TemperatureScale.Fahrenheit, TemperatureScale.Celsius) => FahrenheitToCelsius(normalized),
            _ => normalized,
        };

        return new TemperatureValue(converted * target.Factor(), target);
    }

    public TemperatureValue As(TemperatureScale target) => As(target.ToUnits());

    public double AsCelsius() => As(TemperatureUnits.Celsius).Value;

    public double AsFahrenheit() => As(TemperatureUnits.Fahrenheit).Value;

    /// <summary>
    /// Parses "23", "23.5C", " 23 F " etc. A trailing scale in the string wins
    /// over <paramref name="fallbackScale"/>.
    /// </summary>
    public static TemperatureValue Parse(string text, TemperatureScale? fallbackScale = null)
    {
        var (number, suffix) = SplitNumericPrefix(text);

        var scale = suffix.Length == 0
            ? fallbackScale ?? TemperatureScale.Celsius
            : TemperatureExtensions.ParseScale(suffix);

        return new TemperatureValue(number, scale.ToUnits());
    }

    private static (double Number, string Suffix) SplitNumericPrefix(string input)
    {
        input = input.Trim();
        var end = 0;
        while (end < input.Length && (char.IsDigit(input[end]) || input[end] == '.' || (end == 0 && input[end] == '-')))
        {
            end++;
        }

        if (end == 0 || !double.TryParse(input[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            throw new GoveeException($"cannot parse '{input}' as a temperature");
        }

        return (number, input[end..].Trim());
    }

    public override string ToString()
    {
        var normalized = Normalize();
        return normalized.Value.ToString("0.####", CultureInfo.InvariantCulture)
            + normalized.Units.Scale().UnitOfMeasurement();
    }
}

public enum HumidityUnits
{
    RelativePercent,
    RelativePercentTimes100,
}

public static class HumidityUnitsExtensions
{
    public static double ToRelativePercent(this HumidityUnits units, double value) => units switch
    {
        HumidityUnits.RelativePercentTimes100 => value / 100.0,
        _ => value,
    };
}
