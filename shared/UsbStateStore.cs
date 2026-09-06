using System.Text.Json;

namespace Nanaininai;

/// <summary>
/// Atomic updates for the USB state file:
/// write temp -> flush to disk -> replace original -> keep backup copy.
/// If state.json is corrupted, attempt recovery from state.backup.json.
/// </summary>
public sealed class UsbStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _statePath;
    private readonly string _backupPath;

    public UsbStateStore(string usbAppDir)
    {
        _statePath = Path.Combine(usbAppDir, FileNames.State);
        _backupPath = Path.Combine(usbAppDir, FileNames.StateBackup);
    }

    public string StatePath => _statePath;

    /// <summary>Load state with backup recovery. Returns null if the USB has no valid state yet.</summary>
    public UsbState? Load()
    {
        var main = TryRead(_statePath);
        if (main is not null) return main;

        var backup = TryRead(_backupPath);
        if (backup is not null)
        {
            try { File.Copy(_backupPath, _statePath, overwrite: true); }
            catch (IOException) { }
            return backup;
        }
        return null;
    }

    /// <summary>Atomically persist new state. Also refreshes the backup copy.</summary>
    public void Save(UsbState state)
    {
        string tmp = _statePath + ".tmp";
        string json = JsonSerializer.Serialize(state, JsonOpts);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(fs);
            writer.Write(json);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, _statePath, overwrite: true);

        try { File.Copy(_statePath, _backupPath, overwrite: true); }
        catch (IOException) { }
    }

    private static UsbState? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<UsbState>(json);
            if (state is null || state.NextComputerNumber < 1) return null;
            return state;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
