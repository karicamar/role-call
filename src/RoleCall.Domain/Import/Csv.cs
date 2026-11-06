using System.Text;

namespace RoleCall.Domain.Import;

public sealed class ImportException(string message) : Exception(message);

/// <summary>
/// RFC 4180 CSV: quoted fields with commas, quotes ("") and line breaks; comma or semicolon separator (German Excel
/// writes semicolons). Limits keep a hostile or broken upload from using up memory.
/// </summary>
public static class Csv
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxRows = 50_000;
    public const int MaxFieldLength = 4_000;

    public sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<Row> Rows);

    public sealed record Row(int Line, IReadOnlyDictionary<string, string> Fields)
    {
        public string Get(string column) => Fields.TryGetValue(column, out var v) ? v : "";
        public string? Optional(string column) => Fields.TryGetValue(column, out var v) && v.Length > 0 ? v : null;
    }

    public static Table Parse(string text, params string[] requiredColumns)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new ImportException($"The file is larger than {MaxBytes / 1024 / 1024} MB.");
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var separator = DetectSeparator(text);
        var records = Records(text, separator).ToList();
        if (records.Count == 0) throw new ImportException("The file is empty.");

        var header = records[0].Fields.Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
        var duplicate = header.GroupBy(h => h).FirstOrDefault(g => g.Count() > 1 && g.Key.Length > 0);
        if (duplicate is not null) throw new ImportException($"Column \"{duplicate.Key}\" appears twice.");
        var missing = requiredColumns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0) throw new ImportException($"Missing column(s): {string.Join(", ", missing)}.");
        if (records.Count - 1 > MaxRows) throw new ImportException($"More than {MaxRows:N0} rows.");

        var rows = new List<Row>();
        foreach (var (line, fields) in records.Skip(1))
        {
            if (fields.All(f => f.Trim().Length == 0)) continue;
            if (fields.Count != header.Count)
                throw new ImportException($"Line {line}: expected {header.Count} fields, found {fields.Count}.");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Count; i++) map[header[i]] = fields[i].Trim();
            rows.Add(new Row(line, map));
        }
        return new Table(header, rows);
    }

    static char DetectSeparator(string text)
    {
        var end = text.IndexOf('\n');
        var first = end < 0 ? text : text[..end];
        return first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
    }

    static IEnumerable<(int Line, List<string> Fields)> Records(string text, char separator)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var quoted = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    quoted = false; i++; continue;
                }
                if (c == '\n') line++;
                Append(field, c, recordLine);
                i++;
                continue;
            }
            if (c == '"' && field.Length == 0) { quoted = true; i++; continue; }
            if (c == separator) { fields.Add(field.ToString()); field.Clear(); i++; continue; }
            if (c == '\r') { i++; continue; }
            if (c == '\n')
            {
                fields.Add(field.ToString()); field.Clear();
                yield return (recordLine, fields);
                fields = [];
                line++; recordLine = line; i++;
                continue;
            }
            Append(field, c, recordLine);
            i++;
        }
        if (quoted) throw new ImportException($"Line {recordLine}: a quoted field is not closed.");
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (recordLine, fields);
        }
    }

    static void Append(StringBuilder field, char c, int line)
    {
        if (field.Length >= MaxFieldLength) throw new ImportException($"Line {line}: a field is longer than {MaxFieldLength} characters.");
        field.Append(c);
    }

    /// <summary>
    /// One CSV field for export. Values that start with = + - @ (or tab/CR) get a leading apostrophe, so a
    /// spreadsheet shows them as text instead of running them as a formula (CSV injection, OWASP).
    /// </summary>
    public static string Field(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', ';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string Line(params string?[] values) => string.Join(",", values.Select(Field));
}
