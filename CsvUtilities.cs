using System.Globalization;

/// <summary>Formats invariant CSV rows and quotes every field consistently.</summary>
public static class CsvUtilities
{
    public static string Escape(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTimeOffset timestamp => timestamp.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    public static string Row(params object?[] values) =>
        string.Join(",", values.Select(Escape));
}
