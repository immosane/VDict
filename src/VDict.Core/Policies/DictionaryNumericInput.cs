using System.Globalization;

namespace VDict.Core.Policies;

public static class DictionaryNumericInput
{
    public static bool IsValidFontSizeIntermediate(string text) =>
        string.IsNullOrEmpty(text) || text.All(character => character is >= '0' and <= '9');

    public static bool IsValidLineHeightIntermediate(string text) =>
        string.IsNullOrEmpty(text) ||
        text.Count(character => character == '.') <= 1 &&
        text.All(character => character is >= '0' and <= '9' || character == '.');

    public static bool TryGetFontSize(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value is >= 10 and <= 48)
        {
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryGetLineHeight(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            value is >= 1.0 and <= 3.0)
        {
            return true;
        }

        value = default;
        return false;
    }

    public static int NormalizeFontSize(string text, int fallback) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, 10, 48)
            : Math.Clamp(fallback, 10, 48);

    public static double NormalizeLineHeight(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? Math.Clamp(value, 1.0, 3.0)
            : Math.Clamp(fallback, 1.0, 3.0);
}
