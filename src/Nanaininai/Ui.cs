using Spectre.Console;
using Spectre.Console.Rendering;

namespace Nanaininai;

/// <summary>All Spectre.Console rendering. Kept separate so logic stays testable.</summary>
public static class Ui
{
    public static void Header(string title, string color = "deepskyblue1")
    {
        AnsiConsole.WriteLine();
        var rule = new Rule($"[{color} bold]{Markup.Escape(title)}[/]")
        {
            Justification = Justify.Center,
            Style = new Style(Color.DeepSkyBlue1),
        };
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
    }

    public static void KeyValueTable(string title, IEnumerable<(string Key, string Value)> rows)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold underline]{Markup.Escape(title)}[/]");
        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("");
        table.AddColumn("");
        foreach (var (key, value) in rows)
        {
            table.AddRow($"[grey]{Markup.Escape(key)}[/]", Markup.Escape(value));
        }
        AnsiConsole.Write(table);
    }

    public static void Info(string message) => AnsiConsole.MarkupLine($"[deepskyblue1]•[/] {Markup.Escape(message)}");
    public static void Ok(string message) => AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
    public static void Warn(string message) => AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(message)}");
    public static void Error(string message)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[red bold]ERROR[/] {Markup.Escape(message)}");
        AnsiConsole.WriteLine();
    }

    public static void Panel(string title, string[] lines, Color? borderColor = null)
    {
        AnsiConsole.WriteLine();
        var panel = new Panel(new Markup(string.Join("\n", lines.Select(l => Markup.Escape(l)))))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(borderColor ?? Color.Red),
            Header = new PanelHeader($"[bold]{Markup.Escape(title)}[/]"),
            Padding = new Padding(2, 1),
        };
        AnsiConsole.Write(panel);
    }

    public static void SuccessPanel(string computerName, string ip, string nextName, string nextIp)
    {
        AnsiConsole.WriteLine();
        var body = new Markup(
            $"[bold]Computer:[/] {Markup.Escape(computerName)}\n" +
            $"[bold]IP:[/]       {Markup.Escape(ip)}\n\n" +
            $"[grey]Next computer:[/]\n" +
            $"{Markup.Escape(nextName)}\n" +
            $"{Markup.Escape(nextIp)}\n\n" +
            $"You can now move the USB drive to the next computer.");
        var panel = new Panel(body)
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Green),
            Header = new PanelHeader("[bold green]CONFIGURATION COMPLETE[/]"),
            Padding = new Padding(2, 1),
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>Numbered step runner producing the [n/7] ... OK/FAIL layout.</summary>
    public static void Step(int index, int total, string description, Func<string> action)
    {
        var status = AnsiConsole.Status();
        AnsiConsole.Markup($"[grey][[{index}/{total}]][/] {Markup.Escape(description.PadRight(34))} ");
        try
        {
            var message = action();
            AnsiConsole.MarkupLine("[green bold]OK[/]");
            if (!string.IsNullOrEmpty(message)) AnsiConsole.MarkupLine($"     [grey]{Markup.Escape(message)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red bold]FAIL[/]");
            AnsiConsole.MarkupLine($"     [red]{Markup.Escape(ex.Message)}[/]");
            throw;
        }
    }

    public static bool Confirm(string prompt, bool defaultValue = true)
    {
        return AnsiConsole.Confirm($"[bold]{Markup.Escape(prompt)}[/]", defaultValue);
    }

    public static AdapterInfo? SelectAdapter(List<AdapterInfo> adapters)
    {
        if (adapters.Count == 1) return adapters[0];
        if (adapters.Count == 0) return null;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold underline]Multiple adapters detected[/]");
        var prompt = new SelectionPrompt<AdapterInfo>
        {
            Title = "Select the network adapter to configure",
            PageSize = 10,
            Converter = a => $"{a.Type} — {a.Description}",
        };
        prompt.AddChoices(adapters);
        return AnsiConsole.Prompt(prompt);
    }
}
