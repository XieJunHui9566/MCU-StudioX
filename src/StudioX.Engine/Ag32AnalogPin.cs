namespace StudioX.Engine;

using System.Globalization;
using System.Text.RegularExpressions;

public sealed record Ag32AnalogPin(int Pin, string Functions)
{
    public int? AdcChannel => Regex.Match(Functions, @"ADC_IN(\d+)", RegexOptions.CultureInvariant) is { Success: true } adc
        ? int.Parse(adc.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    public string? ComparatorInput => Regex.Match(Functions, @"CMP_PA\d+") is { Success: true } cmp ? cmp.Value : null;
    public bool Supports(string function) => Functions.Split(['_', '/'], StringSplitOptions.TrimEntries).Contains(function, StringComparer.Ordinal);

    public string ActiveFunctions(Ag32AnalogSettings settings) => ActiveFunctions(settings, null);

    public string ActiveFunctions(Ag32AnalogSettings settings, Ag32Setup? setup)
    {
        var names = new List<string>();
        if (AdcChannel is int ch && (settings.AdcChannels & (1u << ch)) != 0)
        {
            names.Add($"ADC_IN{ch}");
        }
        if (settings.Dac0 && Supports("DAC0"))
        {
            names.Add("DAC0");
        }
        if (settings.Dac1 && Supports("DAC1"))
        {
            names.Add("DAC1");
        }
        if (settings.Comparator && ComparatorInput is string input && (setup?.UsesComparatorPin(input) ?? true))
        {
            names.Add(input);
        }
        return string.Join(" / ", names);
    }
}
