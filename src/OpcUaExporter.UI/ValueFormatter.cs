using System.Globalization;

namespace OpcUaExporter.UI;

/// <summary>
/// Display text for tag values, shared by the readings table, the trend legend
/// and the tag tree so a value looks the same everywhere and does not depend on
/// the machine's regional settings.
/// </summary>
public static class ValueFormatter
{
    /// <summary>
    /// Fixed, culture-independent 24-hour format for DateTime values — the same
    /// format the DateTime write field accepts. Values are shown as the server
    /// sent them (UTC), so a written value reads back unchanged.
    /// </summary>
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Formats a tag value for display; <see langword="null"/> becomes <paramref name="nullText"/>.</summary>
    public static string Format(object? value, string nullText = "")
    {
        return value switch
        {
            null => nullText,
            DateTime dt => dt.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.UtcDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? nullText
        };
    }
}
