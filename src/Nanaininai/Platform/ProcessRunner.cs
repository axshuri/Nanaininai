using System.Diagnostics;
using System.Text;

namespace Nanaininai.Platform;

/// <summary>
/// Runs external Windows tools (netsh, powershell, arp) with strict argument
/// arrays. Never builds shell command lines from raw user input: every caller
/// must pass values that passed whitelist validation first.
/// </summary>
public static class ProcessRunner
{
    public sealed class ProcessResult
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
        public bool Success => ExitCode == 0;
        public string Combined => string.Join(Environment.NewLine, new[] { StdOut, StdErr }.Where(s => s.Length > 0));
    }

    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Console.OutputEncoding,
            StandardErrorEncoding = Console.OutputEncoding
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult { ExitCode = -1, StdErr = $"Could not start {fileName}: {ex.Message}" };
        }

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        var finished = await Task.WhenAny(Task.Run(() => proc.WaitForExit(timeoutMs)), Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (!proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new ProcessResult { ExitCode = -2, StdErr = $"{fileName} timed out after {timeoutMs} ms." };
        }

        return new ProcessResult
        {
            ExitCode = proc.ExitCode,
            StdOut = await outTask.ConfigureAwait(false),
            StdErr = await errTask.ConfigureAwait(false)
        };
    }

    public static Task<ProcessResult> RunNetshAsync(IReadOnlyList<string> args, int timeoutMs = 30_000)
        => RunAsync("netsh", args, timeoutMs);

    /// <summary>Runs PowerShell with a fixed script. Parameters must already be validated/escaped by callers.</summary>
    public static Task<ProcessResult> RunPowerShellAsync(string script, int timeoutMs = 60_000)
        => RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }, timeoutMs);

    public static Task<ProcessResult> RunArpAsync() => RunAsync("arp", new[] { "-a" }, 15_000);
}
