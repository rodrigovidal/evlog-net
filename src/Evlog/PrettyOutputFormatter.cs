using System.Text;

namespace Evlog;

/// <summary>
/// Formats a wide event as colored, human-readable console output.
/// </summary>
public static class PrettyOutputFormatter
{
    private const string Reset = "\x1B[0m";
    private const string Dim = "\x1B[2m";
    private const string Red = "\x1B[31m";
    private const string Green = "\x1B[32m";
    private const string Yellow = "\x1B[33m";
    private const string Cyan = "\x1B[36m";
    private const string Gray = "\x1B[90m";

    public static string Format(RequestLogger logger, int status, double durationMs)
    {
        var sb = new StringBuilder(256);

        var level = WideEventWriter.DetermineLevel(status, logger.GetError());
        var duration = WideEventWriter.FormatDuration(durationMs);

        // Header line: METHOD /path STATUS duration
        sb.Append(' ');

        if (logger.Method is not null)
        {
            sb.Append(logger.Method).Append(' ');
        }
        if (logger.Path is not null)
        {
            sb.Append(logger.Path).Append(' ');
        }

        var statusColor = status >= 400 ? Red : Green;
        sb.Append(statusColor).Append(status).Append(Reset).Append(' ');
        sb.Append(Dim).Append(duration).Append(Reset);
        sb.AppendLine();

        // Collect all lines to display
        var lines = new List<(string key, string value)>();

        // Context entries
        foreach (var entry in logger.GetEntries())
        {
            lines.Add((entry.Key, FormatEntryValue(entry)));
        }

        // Request logs
        foreach (var log in logger.GetRequestLogs())
        {
            lines.Add((log.Level.ToString().ToLowerInvariant(), log.Message));
        }

        // Service
        lines.Add(("service", logger.Service));

        // Write tree
        for (int i = 0; i < lines.Count; i++)
        {
            bool isLast = i == lines.Count - 1;
            string prefix = isLast ? "\u2514\u2500" : "\u251C\u2500";
            sb.Append("  ")
              .Append(Dim).Append(prefix).Append(Reset)
              .Append(' ')
              .Append(Cyan).Append(lines[i].key).Append(':').Append(Reset)
              .Append(' ')
              .Append(lines[i].value);

            if (!isLast) sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatEntryValue(ContextEntry entry) => entry.Kind switch
    {
        ContextValueKind.String => entry.StringValue ?? "null",
        ContextValueKind.Int => entry.IntValue.ToString(),
        ContextValueKind.Long => entry.LongValue.ToString(),
        ContextValueKind.Double => entry.DoubleValue.ToString(),
        ContextValueKind.Bool => entry.BoolValue ? "true" : "false",
        ContextValueKind.JsonFragment => "{...}",
        _ => "?",
    };
}
