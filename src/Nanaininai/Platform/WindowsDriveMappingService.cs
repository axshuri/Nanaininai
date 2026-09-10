using Nanaininai.Core.Network;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;

namespace Nanaininai.Platform;

public sealed class WindowsDriveMappingService : IDriveMappingService
{
    private readonly ILogService _log;
    public WindowsDriveMappingService(ILogService log) => _log = log;

    private const int RESOURCETYPE_DISK = 1;
    private const int CONNECT_UPDATE_PROFILE = 0x1;
    private const int CONNECT_INTERACTIVE = 0x10;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_ALREADY_ASSIGNED = 85;
    private const int ERROR_BAD_NET_NAME = 67;
    private const int ERROR_NO_NETWORK = 1222;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(ref NETRESOURCE resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(string localName, int flags, bool force);

    public Task<List<DriveMapping>> ListMappedDrivesAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            var list = new List<DriveMapping>();
            if (!OperatingSystem.IsWindows()) return list;
            for (var c = 'A'; c <= 'Z'; c++)
            {
                var d = c;
                var letter = $"{d}:";
                try
                {
                    if (Directory.Exists(letter) || DriveInfo.GetDrives().Any(x => string.Equals(x.Name, letter, StringComparison.OrdinalIgnoreCase)))
                    {
                        var unc = QueryRemoteName(letter);
                        list.Add(new DriveMapping { Letter = letter, RemotePath = unc ?? "(local)", Persistent = true, Status = "OK" });
                    }
                }
                catch { }
            }
            return list;
        }, ct);

    private static string? QueryRemoteName(string letter)
    {
        try
        {
            var info = DriveInfo.GetDrives().FirstOrDefault(x => string.Equals(x.Name, letter + "\\", StringComparison.OrdinalIgnoreCase));
            return info?.VolumeLabel is null ? info?.Name : info.Name;
        }
        catch
        {
            return null;
        }
    }

    public Task<OperationResult> MapDriveAsync(string letter, string remotePath, bool persistent, CredentialSet? credentials, OperationContext ctx, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
                return OperationResult.Fail("This operation requires Windows.");

            if (!System.Text.RegularExpressions.Regex.IsMatch(letter, "^[A-Za-z]:$"))
                return OperationResult.Fail($"'{letter}' is not a valid drive letter. Expected e.g. Z:");
            if (!StrictInput.IsValidUncPath(remotePath))
                return OperationResult.Fail($"'{remotePath}' is not a valid network path.", "Expected \\\\server\\share.");

            if (ctx.DryRun)
                return OperationResult.Ok("DRY RUN - no changes were made.",
                    $"1. Map {letter.ToUpperInvariant()} to {remotePath}\n2. Persistent: {(persistent ? "YES (reconnect at sign-in)" : "no")}" +
                    (credentials is null ? "\n3. Use current Windows credentials" : "\n3. Use the alternate credentials provided interactively"));

            if (!ctx.UserConfirmed)
                return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

            var res = new NETRESOURCE
            {
                Scope = 0,
                Type = RESOURCETYPE_DISK,
                DisplayType = 0,
                Usage = 0,
                LocalName = letter.ToUpperInvariant(),
                RemoteName = remotePath
            };

            var password = credentials is null ? null : new string(credentials.Password);
            try
            {
                var rc = WNetAddConnection2(ref res, password, credentials?.UserName, persistent ? CONNECT_UPDATE_PROFILE : 0);
                switch (rc)
                {
                    case 0:
                        _log.Success("drive.map", letter.ToUpperInvariant(), $"{remotePath} persistent={persistent}");
                        return OperationResult.Ok("Drive mapped.",
                            $"{letter.ToUpperInvariant()} → {remotePath}\n\nPersistent:\n{(persistent ? "YES" : "no")}");
                    case ERROR_ALREADY_ASSIGNED:
                        return OperationResult.Fail($"Drive {letter.ToUpperInvariant()} is already in use.", "Disconnect it first (Drives section) or choose another letter.", rc);
                    case ERROR_BAD_NET_NAME:
                        return OperationResult.Fail($"The network path {remotePath} was not found.", "Possible causes:\n- Share name misspelled\n- Share does not exist on the server\n- Server offline", rc);
                    case ERROR_LOGON_FAILURE:
                    case ERROR_ACCESS_DENIED:
                        return OperationResult.Fail("Authentication failed.", "Possible causes:\n- Wrong username or password\n- The account has no share permission\n- The server rejects this account", rc);
                    case ERROR_NO_NETWORK:
                        return OperationResult.Fail("The network is unreachable.", "Possible causes:\n- Cable unplugged\n- Wrong IP configuration\n- Target computer offline", rc);
                    default:
                        return OperationResult.Fail($"Mapping failed (code {rc}).", $"Windows returned error {rc}. " + Humanize(rc), rc);
                }
            }
            catch (Exception ex)
            {
                return OperationResult.Fail("Mapping failed.", ex.Message);
            }
            finally
            {
                password = null; // drop the temporary plaintext copy
            }
        }, ct);
    }

    private static string Humanize(int rc) => rc switch
    {
        53 => "The network path was not found - check connectivity and the Server service.",
        67 => "",
        86 => "The network password was wrong.",
        1219 => "Multiple connections to the same server with different accounts are not allowed.",
        1223 => "The operation was canceled.",
        _ => ""
    };

    public Task<OperationResult> UnmapDriveAsync(string letter, OperationContext ctx, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
                return OperationResult.Fail("This operation requires Windows.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(letter, "^[A-Za-z]:$"))
                return OperationResult.Fail($"'{letter}' is not a valid drive letter.");
            if (ctx.DryRun)
                return OperationResult.Ok("DRY RUN - no changes were made.", $"1. Disconnect drive {letter.ToUpperInvariant()}.");
            if (!ctx.UserConfirmed)
                return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

            var rc = WNetCancelConnection2(letter.ToUpperInvariant(), CONNECT_UPDATE_PROFILE, force: true);
            if (rc == 0)
            {
                _log.Success("drive.unmap", letter.ToUpperInvariant(), "disconnected");
                return OperationResult.Ok($"Drive {letter.ToUpperInvariant()} disconnected.");
            }
            return OperationResult.Fail($"Could not disconnect {letter.ToUpperInvariant()}.", Humanize(rc) + $" (code {rc})", rc);
        }, ct);
    }
}
