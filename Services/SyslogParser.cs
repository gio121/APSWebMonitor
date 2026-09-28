using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace ApsMonitor.Services;

public sealed record SyslogEntry(string Timestamp, string Host, string Process, string Pid, string Message, string RawLine)
{
    public DateTime? Date { get; init; }
}

public static partial class SyslogParser
{
    // Traditional syslog timestamps contain no year or time zone. Keep them as recorded.
    [GeneratedRegex(@"^(?<date>[A-Z][a-z]{2}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2})\s+(?<host>\S+)\s+(?<process>[^\s:\[]+)(?:\[(?<pid>\d+)\])?:\s?(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^(?:<\d+>)?(?<date>\d{4}-\d{2}-\d{2}T\S+)\s+(?<host>\S+)\s+(?<process>[^\s:\[]+)(?:\[(?<pid>\d+)\])?:\s?(?<message>.*)$")]
    private static partial Regex IsoPattern();

    public static bool MatchesDateRange(SyslogEntry entry, DateTime? start, DateTime? end) =>
        !entry.Date.HasValue || ((!start.HasValue || entry.Date.Value.Date >= start.Value.Date)
            && (!end.HasValue || entry.Date.Value.Date <= end.Value.Date));

    public static List<SyslogEntry> Parse(string content)
    {
        var entries = new List<SyslogEntry>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (entries.Count == 0 && IsHeader(line)) continue;
            var match = LinePattern().Match(line);
            if (!match.Success) match = IsoPattern().Match(line);
            if (!match.Success && TryLegacy(line, out var legacy))
            {
                entries.Add(legacy!);
                continue;
            }
            entries.Add(match.Success
                ? new SyslogEntry(match.Groups["date"].Value, match.Groups["host"].Value,
                    match.Groups["process"].Value, match.Groups["pid"].Value,
                    match.Groups["message"].Value, line) { Date = ParseDate(match.Groups["date"].Value) }
                : new("", "", "", "", line, line));
        }
        return entries;
    }

    private static DateTime? ParseDate(string value)
    {
        // No inferred year for traditional syslog. ISO dates retain the source calendar date.
        if (value.Length == 0 || !char.IsDigit(value[0])) return null;
        var date = value.Split('T', ' ')[0];
        return DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Date : null;
    }

    private static bool IsHeader(string line)
    {
        var fields = line.Split(',');
        if (fields.Length < 4) return false;
        var first = fields[0].Trim().Trim('"');
        var second = fields[1].Trim().Trim('"');
        return (first.Equals("Date", StringComparison.OrdinalIgnoreCase) && second.Equals("Time", StringComparison.OrdinalIgnoreCase))
            || (first.Equals("Fecha", StringComparison.OrdinalIgnoreCase) && second.Equals("Hora", StringComparison.OrdinalIgnoreCase))
            || (first == "Fecha y hora" && second == "Equipo");
    }

    private static bool TryLegacy(string line, out SyslogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        string[] fields;
        if (line.Contains(','))
        {
            using var parser = new TextFieldParser(new StringReader(line));
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            try { fields = parser.ReadFields() ?? []; }
            catch (MalformedLineException) { return false; }
        }
        else fields = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 5 && (fields[0].Contains('T') || fields[0].Contains(' ')))
        {
            entry = new(fields[0], fields[1], fields[2], fields[3], fields[4], line) { Date = ParseDate(fields[0]) };
            return true;
        }
        if (fields.Length < 4 || ParseDate(fields[0]) is not { } date) return false;
        entry = new($"{fields[0]} {fields[1]}", "", fields[2], "", string.Join(",", fields.Skip(3)), line) { Date = date };
        return true;
    }
}
