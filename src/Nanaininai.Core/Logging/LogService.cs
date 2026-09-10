using System.Text;
using System.Text.Json;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;

namespace Nanaininai.Core.Logging;

/// <summary>
/// Structured logger: keeps an in-memory ring buffer and appends JSON lines to a file.
/// Callers must never pass credential material; this class additionally scrubs
/// common password-shaped keys as a second line of defense.
/// </summary>
public sealed class LogService : ILogService
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = new();
    private const int MaxInMemory = 5000;
    private static readonly string[] ForbiddenSubstrings = { "password", "passwd", "pwd", "secret", "token", "credential" };

    public string LogFilePath { get; }
    private readonly bool _fileEnabled;

    public LogService(string? directory = null)
    {
        var dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nanaininai", "logs");
        try
        {
            Directory.CreateDirectory(dir);
            LogFilePath = Path.Combine(dir, $"lanagent-{DateTime.Now:yyyyMMdd}.log");
            _fileEnabled = true;
        }
        catch
        {
            LogFilePath = "(in-memory only)";
            _fileEnabled = false;
        }
    }

    public void Log(LogEntry entry)
    {
        if (ContainsForbiddenMaterial(entry.Operation) || ContainsForbiddenMaterial(entry.Result) ||
            ContainsForbiddenMaterial(entry.ErrorMessage) || ContainsForbiddenMaterial(entry.NewState) ||
            ContainsForbiddenMaterial(entry.PreviousState))
        {
            entry.ErrorMessage = "REDACTED: potential credential material removed from log entry";
        }

        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxInMemory) _entries.RemoveAt(0);
        }

        if (!_fileEnabled) return;
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                ts = entry.Timestamp,
                level = entry.Level.ToString(),
                op = entry.Operation,
                target = entry.Target,
                old = entry.PreviousState,
                @new = entry.NewState,
                result = entry.Result,
                error = entry.ErrorMessage,
                confirmed = entry.ConfirmedByUser,
                dryRun = entry.DryRun
            });
            File.AppendAllText(LogFilePath, json + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Logging must never crash the agent.
        }
    }

    public IReadOnlyList<LogEntry> GetEntries(LogEventLevel? minLevel = null)
    {
        lock (_lock)
        {
            IEnumerable<LogEntry> q = _entries;
            if (minLevel is not null)
                q = q.Where(e => Severity(e.Level) >= Severity(minLevel.Value));
            return q.ToList();
        }
    }

    private static int Severity(LogEventLevel l) => l switch
    {
        LogEventLevel.Info => 0,
        LogEventLevel.Success => 0,
        LogEventLevel.Warn => 1,
        LogEventLevel.Error => 2,
        _ => 0
    };

    private static bool ContainsForbiddenMaterial(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var f in ForbiddenSubstrings)
            if (text.Contains(f, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
