namespace Nanaininai;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Any argument = CLI mode (scriptable). No arguments = interactive TUI.
        if (args.Length > 0)
            return await CliHost.RunAsync(args).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Nanaininai is a Windows LAN administration tool.");
            Console.Error.WriteLine("Run it on Windows (Windows 10/11) as lanagent.exe. On this platform only limited diagnostics are available.");
            return 1;
        }

        return TUI.TuiApp.Run();
    }
}
