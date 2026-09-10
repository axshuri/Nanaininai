using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;
using Nanaininai.Services;
using Nanaininai.Platform;
using Terminal.Gui;

namespace Nanaininai.TUI;

/// <summary>
/// Interactive TUI shell. Contains no networking logic - all work is delegated
/// to the Core/Services engine and rendered here.
///
/// Threading rules (these previously caused crashes after applying settings):
/// - Every view mutation and every dialog is marshaled to the main loop thread.
/// - Background work that can throw goes through <see cref="Background"/>.
/// - A monotonically increasing render generation discards stale screen renders
///   when the user switches sections while data is still loading.
/// </summary>
public static class TuiApp
{
    internal static AgentContext _ctx = null!;
    private static ListView _menu = null!;
    private static FrameView _content = null!;
    private static Label _statusBar = null!;
    private static int _selected;

    private static int _mainThreadId = -1;
    private static int _renderGen;

    // Loading spinner (driven by a main-loop timer, stopped when the screen renders).
    private static object? _spinnerToken;
    private static Label? _spinnerLabel;
    private static int _spinnerFrame;
    private static readonly string[] SpinnerFrames = { "-", "\\", "|", "/" };

    // Simple TTL result cache so switching sections is instant.
    // Cleared on F5 and after every successful configuration change.
    private sealed class CacheEntry { public DateTimeOffset At; public object? Value; public Task? InFlight; }
    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, CacheEntry> _cache = new();
    internal static readonly TimeSpan CacheShort = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan CacheMedium = TimeSpan.FromSeconds(15);

    /// <summary>Reload the currently visible screen with fresh data (used after successful operations).</summary>
    internal static void LoadScreenRefresh()
    {
        ClearCache();
        LoadScreen(_selected);
    }

    private static readonly (string Title, Func<FrameView, Task> Render)[] Screens =
    {
        ("Dashboard", ScreenDashboard.Show),
        ("Network", ScreenNetwork.Show),
        ("Planner", ScreenPlanner.Show),
        ("Discovery", ScreenDiscovery.Show),
        ("Diagnostics", ScreenDiagnostics.Show),
        ("Check LAN", ScreenCheckLan.Show),
        ("Sharing", ScreenSharing.Show),
        ("Firewall", ScreenFirewall.Show),
        ("Drives", ScreenDrives.Show),
        ("Naming", ScreenNaming.Show),
        ("Logs", ScreenLogs.Show),
    };

    public static int Run()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        _ctx = new AgentContext();

        // Administrator privileges: never elevate silently.
        // A native Windows dialog is shown because Terminal.Gui dialogs cannot
        // be displayed before Application.Init(). Choosing "Yes" relaunches the
        // app elevated, which makes Windows show the UAC permission prompt.
        if (!Elevation.IsAdmin())
        {
            var choice = Elevation.ShowElevationDialog(
                "Nanaininai",
                "Administrator privileges are required for configuration changes.\n\n" +
                "Current mode: LIMITED (read-only diagnostics available)\n\n" +
                "Yes    = Restart as Administrator (Windows shows the permission prompt)\n" +
                "No     = Continue in Read-Only Mode\n" +
                "Cancel = Exit");
            if (choice == Elevation.ElevationChoice.Exit) return 0;
            if (choice == Elevation.ElevationChoice.Elevate)
            {
                var started = Elevation.RestartAsAdmin();
                if (started == Elevation.StartResult.Started)
                    return 0; // UAC prompt shown; the elevated instance takes over.
                // UAC declined or restart failed: fall through to limited mode.
            }
        }

        Application.Init();
        var win = new Window { Title = "Nanaininai - Windows LAN Agent" };

        _menu = new ListView
        {
            X = 0,
            Y = 0,
            Width = 22,
            Height = Dim.Fill(1),
            AllowsMarking = false
        };
        _menu.SetSource(Screens.Select((s, i) => $"[{i + 1}] {s.Title}").ToList());
        _menu.SelectedItem = 0;
        _menu.OpenSelectedItem += _ => LoadScreen(_menu.SelectedItem);

        _content = new FrameView("Dashboard")
        {
            X = 22,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        _statusBar = new Label(GetStatusText()) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

        win.Add(_menu, _content, _statusBar);

        // Keyboard-first navigation. Number shortcuts (and Q) are ignored while
        // the user is typing inside a text field or text view (so typing a
        // number in a form never switches the section) and while a modal
        // dialog is open (so keys never act behind a dialog).
        win.KeyDown += e =>
        {
            if (!ReferenceEquals(Application.Current, win)) return;
            var key = e.KeyEvent.Key;
            if (key == Key.Q && !IsTextInputFocused())
            {
                if (Ask(() => MessageBox.Query("Quit", "Exit Nanaininai?", "Yes", "No")) == 0)
                {
                    Application.RequestStop();
                }
                else e.Handled = true;
            }
            else if (key >= Key.D1 && key <= Key.D9 && !IsTextInputFocused())
            {
                var idx = (int)key - (int)Key.D1;
                if (idx < Screens.Length)
                {
                    ClearCache();
                    LoadScreen(idx);
                    e.Handled = true;
                }
            }
            else if (key == Key.F5)
            {
                ClearCache();
                LoadScreen(_menu.SelectedItem);
                e.Handled = true;
            }
        };

        LoadScreen(0);
        Application.Run(win);
        Application.Shutdown();
        return 0;
    }

    private static string GetStatusText() =>
        $" Mode: {(Elevation.IsAdmin() ? "ADMIN" : "LIMITED (read-only)")}  |  Host: {Environment.MachineName}  |  [1-9] sections  [Enter] open  [F5] refresh  [Q] quit";

    private static void LoadScreen(int index)
    {
        _selected = index;
        var (title, render) = Screens[index];
        var gen = Interlocked.Increment(ref _renderGen);

        Ui(() =>
        {
            if (gen != Volatile.Read(ref _renderGen)) return;
            _content.Title = title.ToUpperInvariant();
            _content.RemoveAll();
            _menu.SelectedItem = index;
            _statusBar.Text = GetStatusText();
            ShowLoading(_content, $"Loading {title} ...");
        });

        // Run async screen rendering without blocking the main loop.
        _ = Task.Run(async () =>
        {
            try
            {
                await render(_content).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("tui.screen", title, ex.Message);
                Ui(() =>
                {
                    if (gen != Volatile.Read(ref _renderGen)) return;
                    StopSpinner();
                    _content.RemoveAll();
                    _content.Add(new Label(1, 1, $"Could not load this section: {ex.Message}") { Width = Dim.Fill() });
                });
                Error(HumanizeError(ex));
            }
        });
    }

    /// <summary>True when the user is typing inside an editable text control.</summary>
    private static bool IsTextInputFocused()
    {
        View? v = Application.Current?.Focused;
        while (v is not null)
        {
            if (v is TextField or TextView) return true;
            v = v.Focused;
        }
        return false;
    }

    internal static string HumanizeError(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("0x800704CF") || ex is System.Net.NetworkInformation.NetworkInformationException)
            return "A network operation failed.\n\nPossible causes:\n- Adapter disconnected\n- Incorrect IP configuration\n- Firewall blocking traffic\n- Target computer offline\n\nTechnical details:\n" + msg;
        return msg + "\n\nTechnical details:\n" + ex.GetType().Name;
    }

    // ------------------------------------------------------------- helpers

    internal static Label AddLabel(View host, int x, int y, string text) =>
        new(text) { X = x, Y = y, Width = Dim.Fill() };

    internal static void ShowBusy(FrameView host, string text)
    {
        var label = new Label(text) { X = 1, Y = 1, Width = Dim.Fill() };
        host.Add(label);
    }

    /// <summary>
    /// Replaces the section content on the main loop thread. If the user has
    /// switched to another section while the data was loading, the stale
    /// render is discarded instead of overwriting the new section.
    /// </summary>
    internal static void InvokeRefresh(FrameView host, Action action)
    {
        var gen = Volatile.Read(ref _renderGen);
        Ui(() =>
        {
            if (gen != Volatile.Read(ref _renderGen)) return;
            StopSpinner();
            host.RemoveAll();
            action();
        });
    }

    // ------------------------------------------------------ threading core

    internal static bool IsUiThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

    /// <summary>Runs an action on the main loop thread (directly when already there).</summary>
    internal static void Ui(Action action)
    {
        if (IsUiThread) action();
        else Application.MainLoop?.Invoke(action);
    }

    /// <summary>
    /// Runs a function on the main loop thread and waits for its result.
    /// Safe to call from background threads; dialogs must always go through
    /// this (MessageBox on a background thread crashes the app).
    /// </summary>
    internal static T Ask<T>(Func<T> func)
    {
        if (IsUiThread) return func();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.MainLoop?.Invoke(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs a task on a background thread; unexpected exceptions are reported
    /// with a dialog (marshaled) instead of crashing the app.
    /// </summary>
    internal static void Background(Func<Task> work) => _ = Task.Run(async () =>
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception ex) { Error(HumanizeError(ex)); }
    });

    internal static async Task UiAsync(Action action) => Ui(action);
    internal static async Task UiAsync(Func<Task> action) => Ui(() => action().GetAwaiter().GetResult());
    internal static async Task<T> UiAsync<T>(Func<T> func) => func();

    // ------------------------------------------------------------- dialogs

    internal static bool Confirm(string title, string message)
        => Ask(() => MessageBox.Query(title, message, "Yes", "No")) == 0;

    internal static void Info(string title, string message)
        => Ask<object?>(() => { MessageBox.Query(title, message, "OK"); return null; });

    internal static void Error(string message)
        => Ask<object?>(() => { MessageBox.ErrorQuery("Error", message, "OK"); return null; });

    internal static void Error(string title, string message)
        => Ask<object?>(() => { MessageBox.ErrorQuery(title, message, "OK"); return null; });

    // ------------------------------------------------------ loading spinner

    private static void ShowLoading(FrameView host, string text)
    {
        StopSpinner();
        _spinnerLabel = new Label(text) { X = 1, Y = 1, Width = Dim.Fill() };
        host.Add(_spinnerLabel);
        _spinnerFrame = 0;
        _spinnerToken = Application.MainLoop?.AddTimeout(TimeSpan.FromMilliseconds(120), _ =>
        {
            var lbl = _spinnerLabel;
            if (lbl is null) return false;
            lbl.Text = $"{SpinnerFrames[_spinnerFrame++ % SpinnerFrames.Length]}  {text}";
            return true;
        });
    }

    private static void StopSpinner()
    {
        if (_spinnerToken is not null)
        {
            try { Application.MainLoop?.RemoveTimeout(_spinnerToken); } catch { }
            _spinnerToken = null;
        }
        _spinnerLabel = null;
    }

    // ---------------------------------------------------------------- cache

    internal static void ClearCache()
    {
        lock (_cacheLock) _cache.Clear();
        Platform.WindowsSystemInfoProvider.InvalidateRuntimeCache();
    }

    /// <summary>
    /// Returns a cached service result when fresh enough; otherwise runs the
    /// factory. Concurrent callers for the same key share one in-flight fetch.
    /// </summary>
    internal static async Task<T> GetCachedAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        CacheEntry entry;
        Task<T> inFlight;
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out var e))
            {
                e = new CacheEntry();
                _cache[key] = e;
            }
            entry = e;
            if (entry.Value is T hit && DateTimeOffset.UtcNow - entry.At < ttl) return hit;
            if (entry.InFlight is Task<T> running)
            {
                inFlight = running;
            }
            else
            {
                inFlight = factory();
                entry.InFlight = inFlight;
            }
        }

        try
        {
            var value = await inFlight.ConfigureAwait(false);
            lock (_cacheLock)
            {
                entry.Value = value;
                entry.At = DateTimeOffset.UtcNow;
                entry.InFlight = null;
            }
            return value;
        }
        catch
        {
            // Never cache failures; let the next caller retry.
            lock (_cacheLock)
            {
                if (ReferenceEquals(entry.InFlight, inFlight)) entry.InFlight = null;
                entry.Value = null;
            }
            throw;
        }
    }

    // ------------------------------------------------------ change pipeline

    internal static async Task<OperationResult> ApplyWithConfirmationAsync(FrameView host, string title, OperationPlan plan, Func<OperationContext, Task<OperationResult>> apply)
    {
        if (!Elevation.IsAdmin())
        {
            var choice = Ask(() => MessageBox.Query(title,
                "Administrator privileges are required for this operation.\n\nCurrent mode: LIMITED",
                "Restart as Administrator", "Cancel"));
            if (choice == 0)
                Elevation.RestartAsAdmin();
            return OperationResult.Fail("Not elevated.");
        }

        var preview = $"NETWORK CONFIGURATION PREVIEW\n\n{plan.Render(false)}\n\nApply changes?";
        if (!Confirm(title, preview)) return OperationResult.Fail("Cancelled by user.");
        var result = await apply(new OperationContext { UserConfirmed = true }).ConfigureAwait(false);
        if (result.Success)
            Info(title, result.Message + (string.IsNullOrEmpty(result.Details) ? "" : "\n\n" + result.Details));
        else
            Error(result.Message + (string.IsNullOrEmpty(result.Details) ? "" : "\n\n" + result.Details));
        return result;
    }
}
