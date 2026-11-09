using System.Globalization;
using System.Text.Json;

namespace Hhub.Core;

public static class MetricFormatter
{
    public static string Format(JsonElement value, string unit = "", CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "—";
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out double number) || !double.IsFinite(number)) return "—";
            string format = unit is " GHz" or " Mbps" ? "0.##" : "0.#";
            return number.ToString(format, culture) + unit;
        }
        return value.ToString() + unit;
    }
}
