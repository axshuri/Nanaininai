using System.IO;
using System.Text;

namespace Nanaininai;

/// <summary>Plain-text log file on the USB: USB:/LabNetwork/Logs/yyyy-MM-dd.log</summary>
public sealed class UsbLogger : IDisposable
{
    private readonly StreamWriter? _writer;
    public string? LogFilePath { get; }

    public UsbLogger(string usbAppDir)
    {
        try
        {
            var dir = Path.Combine(usbAppDir, FileNames.LogsDir);
            Directory.CreateDirectory(dir);
            LogFilePath = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");
            _writer = new StreamWriter(LogFilePath, append: true);
        }
        catch (IOException)
        {
            _writer = null;
        }
        catch (UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    public void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        try
        {
            _writer?.WriteLine(line);
            _writer?.Flush();
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch (IOException) { }
    }
}
