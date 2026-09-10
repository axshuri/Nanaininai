using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nanaininai.Platform;

/// <summary>Administrator privilege detection. Never elevates silently.</summary>
public static class Elevation
{
    /// <summary>Choice returned by the native start-up elevation dialog.</summary>
    public enum ElevationChoice { Elevate, ContinueLimited, Exit }

    // user32.MessageBoxW flags and results.
    private const uint MbYesNoCancel = 0x00000003;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbTopmost = 0x00040000;
    private const int IdYes = 6;
    private const int IdNo = 7;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>
    /// Shows a native Windows dialog asking the user whether to restart with
    /// Administrator rights. Must be used before the TUI initializes because
    /// Terminal.Gui dialogs require Application.Init() first. Choosing "Yes"
    /// leads to the standard Windows UAC permission prompt (via RestartAsAdmin).
    /// </summary>
    public static ElevationChoice ShowElevationDialog(string title, string message)
    {
        if (!OperatingSystem.IsWindows()) return ElevationChoice.ContinueLimited;
        var result = MessageBoxW(IntPtr.Zero, message, title, MbYesNoCancel | MbIconWarning | MbTopmost);
        return result switch
        {
            IdYes => ElevationChoice.Elevate,
            IdNo => ElevationChoice.ContinueLimited,
            _ => ElevationChoice.Exit
        };
    }

    public static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public enum StartResult { Started, DeclinedByUser, Failed }

    /// <summary>
    /// Relaunches the current executable with the UAC elevation prompt.
    /// Returns Started when the shell accepted the launch (the UAC dialog is
    /// shown to the user by Windows and the current process should exit).
    /// </summary>
    public static StartResult RestartAsAdmin(string? arguments = null)
    {
        if (!OperatingSystem.IsWindows()) return StartResult.Failed;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return StartResult.Failed;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments ?? "",
                UseShellExecute = true,
                Verb = "runas"
            };
            try
            {
                Process.Start(psi);
                return StartResult.Started;
            }
            catch (Exception ex) when (
                ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase))
            {
                return StartResult.Failed;
            }
        }
        catch (Win32Exception)
        {
            // User cancelled the UAC prompt.
            return StartResult.DeclinedByUser;
        }
        catch
        {
            return StartResult.Failed;
        }
    }
}
