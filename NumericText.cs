using System;
using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>
/// Parses UI decimal input without treating either separator as a thousands marker.
/// Display formatting follows the PC's region; typing accepts both dot and comma.
/// </summary>
internal static class NumericText
{
    public static bool TryParseDouble(string? input, out double value)
    {
        value = 0;
        string text = input?.Trim() ?? "";
        if (text.Length == 0 || (text.Contains('.') && text.Contains(','))) return false;

        // A single separator is always decimal here, never thousands grouping.
        // This also avoids locale-specific ambiguity in scientific notation.
        const NumberStyles styles = NumberStyles.Float;
        bool parsed = text.Contains('.') || text.Contains(',')
            ? double.TryParse(text.Replace(',', '.'), styles, CultureInfo.InvariantCulture, out value)
            : double.TryParse(text, styles, CultureInfo.CurrentCulture, out value) ||
              double.TryParse(text, styles, CultureInfo.InvariantCulture, out value);
        return parsed && double.IsFinite(value);
    }

    public static bool TryParseFloat(string? input, out float value)
    {
        value = 0;
        if (!TryParseDouble(input, out double parsed) ||
            parsed is > float.MaxValue or < -float.MaxValue) return false;
        value = (float)parsed;
        return float.IsFinite(value);
    }
}
