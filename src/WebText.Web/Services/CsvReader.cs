using System.Text;

namespace WebText.Web.Services;

/// <summary>Minimal RFC 4180 CSV reader (quoted fields, embedded commas, quotes and newlines).</summary>
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
                    row = new List<string>();
                    break;
                default: field.Append(c); break;
            }
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
        return rows;
    }

    public static string Escape(string? value)
    {
        value ??= "";
        // Guard against spreadsheet formula injection when the export is opened in Excel.
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
