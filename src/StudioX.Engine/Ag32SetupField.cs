namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public sealed record Ag32SetupField(string Key, string Label, string Default, string[]? Choices = null,
    int? Minimum = null, int? Maximum = null);
