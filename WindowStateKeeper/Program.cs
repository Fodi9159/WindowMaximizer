using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

// WindowStateKeeper: remembers whether each top-level window was closed
// maximized, and re-maximizes it the next time it opens. Works for classic
// Win32 windows and UWP apps (ApplicationFrameHost frame windows).
//
// Runs in the system tray. Right-click the icon for the menu: "Start with
// Windows (elevated)" (creates/removes a logon scheduled task), "Restart as
// administrator", and "Exit".
//
// State is stored per window "key" in HKCU\Software\WindowStateKeeper.
// A window's key is "<exe>|<class>" (plus "|<title>" for UWP frame windows,
// which share exe/class across apps, and Explorer windows, which share
// exe/class across folders - the title is what separates them).
//
// NOTE: window text is read with SendMessageTimeout(SMTO_ABORTIFHUNG) and
// only for UWP frame and Explorer windows, because plain GetWindowText blocks
// forever if any window on the system has a hung thread.

internal static class Program
{
    // ---- P/Invoke ----
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    // Cheap process census for the re-launch poller (see WatcherLoop): EnumProcesses
    // returns a bare pid list (~microseconds); names are resolved once per pid.
    [DllImport("kernel32.dll")]
    private static extern bool EnumProcesses(uint[] processIds, int arraySizeBytes, out uint bytesReturned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROC_QUERY_LIMITED_INFO = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int STATE_SYSTEM_MINIMIZED = 0x8;
    private const int STATE_SYSTEM_MAXIMIZED = 0x10;

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint DWMWA_CLOAKED = 14;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWNORMAL = 1;
    private const int WPF_RESTORETOMAXIMIZED = 0x0002;

    private const uint WM_GETTEXT = 0x000D;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint TextTimeoutMs = 300;

    private const string RegRoot = @"Software\WindowStateKeeper";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WindowStateKeeper";
    private const string AutoStartTaskName = "WindowStateKeeper";
    private const string UwpFrameExe = "ApplicationFrameHost.exe";
    private static readonly TimeSpan RestoreGiveUp = TimeSpan.FromSeconds(15);
    // Safety-net poll cadence. Detection is event-driven (SetWinEventHook wakes the
    // watcher the moment a window opens/closes or maximizes), so this only covers
    // events WinEvent may miss under load.
    private const int FallbackPollMs = 2000;
    // While a window was recently maximized we watch its placement at this rate:
    // no WinEvent fires for maximize/un-maximize on Windows 11, so a prompt poll
    // is the only way to observe a deliberate un-maximize (and separate it from
    // a UWP close-animation revert).
    private const int FastPollMs = 100;

    private sealed class WinInfo
    {
        public required uint Pid;
        public required string Exe;
        public required string Class;
        public required string Title;
        public required bool Maximized;
        public required int ShowCmd;
        // Per-window disambiguation: the first live window of a base key is instance
        // 1 (plain key), the second 2, etc. Assigned when the window is first seen
        // and kept until it closes, so z-order churn never swaps identities. A closed
        // window's index is freed, so a reopened window takes it back and finds its
        // own record. Two same-title windows (two "Untitled - Notepad") keep separate
        // records instead of sharing one key.
        // ponytail: positional allocation; if two same-title windows close with
        // different states and reopen in swapped order, state follows order (the app
        // itself decides which window is "the same" - unknowable).
        public required int Instance;
        // WPF windows carry a per-launch random GUID in their class (HwndWrapper[App;;<guid>]);
        // strip it so the key is stable across launches and "closed maximized" restores.
        // ponytail: only strips a 36-char hex GUID, other classes untouched.
        public string BaseKey => BuildBaseKey(Exe, Class, Title);
        // Registry key: base key plus an instance suffix for siblings of the same
        // base key. Instance 1 keeps the plain key, so records written before
        // disambiguation keep working unchanged.
        public string Key => Instance <= 1 ? BaseKey : $"{BaseKey}|#{Instance}";
    }

    private static string BuildBaseKey(string exe, string cls, string title) =>
        string.IsNullOrEmpty(title)
            ? $"{exe}|{Regex.Replace(cls, @";[0-9a-fA-F-]{36}", "")}"
            : $"{exe}|{Regex.Replace(cls, @";[0-9a-fA-F-]{36}", "")}|{title}";

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Shell_DwmTabWindow",
    };

    private static NotifyIcon? _notifyIcon;
    private static Icon? _trayIcon;
    private static AutoResetEvent? _wakeEvent;
    // Single-instance mutex; held for the whole process lifetime (see Main).
    private static Mutex? _instanceMutex;
    // Windows that just became visible, queued by the WinEvent callback for
    // immediate restore (see WatcherLoop). Single writer (the message-pump
    // thread), single reader (the watcher thread).
    private static readonly ConcurrentQueue<IntPtr> ShowQueue = new();
    private static IntPtr _winEventHook;
    private static WinEventDelegate? _winEventProc; // kept alive: the OS holds a reference to it
    private static volatile bool _displayChanged;   // set by WM_DISPLAYCHANGE on the message pump
    // Gates the 50ms re-launch poller: it only needs to scan while some window has a
    // maximized intent (re-launch re-max only matters then). The watcher loop sets
    // this each pass; the poller skips the Process scan when idle so it sleeps.
    private static volatile bool _spikeActive;

    [STAThread]
    private static int Main(string[] args)
    {
        bool verbose = args.Contains("--verbose");

        // Headless autostart control (also used by the tray menu, same code path):
        //   --autostart on|off   -> sets/removes the Run key, exit code 0 = ok
        //   --autostart query    -> exit code 0 = enabled, 1 = disabled
        if (args.Contains("--autostart"))
        {
            string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            try
            {
                switch (mode)
                {
                    case "on": return SetAutoStart(true) ? 0 : 1;
                    case "off": return SetAutoStart(false) ? 0 : 1;
                    case "query": return IsAutoStartEnabled() ? 0 : 1;
                    default: return 2;
                }
            }
            catch (Exception ex)
            {
                Log("autostart CLI failed: " + ex.Message);
                return 1;
            }
        }

        // Single-instance guard: a second copy would fight this one over window
        // placement and interleave log writes. "Local\" scopes the mutex to this
        // logon session, so two Windows users can each run their own copy.
        // Placed AFTER the --autostart CLI block above so toggling autostart
        // while the app runs still works (the CLI path exits before this).
        // Held in a field for the process lifetime - disposing it would release it.
        _instanceMutex = new Mutex(true, @"Local\WindowStateKeeper.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            Log("second instance detected; exiting");
            return 0;
        }

        // Event-driven detection: wake the watcher the moment a window is created,
        // shown, hidden, destroyed, or toggles maximize/minimize. The callback runs
        // on this (message-pumping) thread and just signals the watcher thread, so
        // all window logic stays on one thread. The fallback poll covers gaps.
        _wakeEvent = new AutoResetEvent(false);
        _winEventProc = WinEventProc;
        _winEventHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_STATECHANGE,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_winEventHook == IntPtr.Zero)
            Log("WARNING: SetWinEventHook failed; relying on the fallback poll only");

        Application.AddMessageFilter(new DisplayChangeFilter());

        // Watcher runs on a background thread so the tray message pump stays responsive.
        new Thread(() =>
        {
            try { WatcherLoop(verbose); }
            catch (Exception ex) { Log("FATAL: " + ex); }
        })
        { IsBackground = true }.Start();

        Application.ThreadException += (_, e) => Log("ThreadException: " + e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ApplicationExit += (_, _) =>
        {
            if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
            if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }
            _trayIcon?.Dispose();
        };

        try
        {
            _trayIcon = LoadIcon();
            _notifyIcon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "WindowStateKeeper",
                ContextMenuStrip = BuildMenu(),
                Visible = true,
            };
        }
        catch (Exception ex)
        {
            Log("tray init failed: " + ex);
            MessageBox.Show("WindowStateKeeper could not start its tray icon:\n" + ex.Message,
                "WindowStateKeeper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        Log($"started (autostart={(IsAutoStartEnabled() ? "on" : "off")}, elevated={IsElevated()})");
        Application.Run(new ApplicationContext());
        return 0;
    }

    // ---- Tray menu ----

    private static ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var startItem = new ToolStripMenuItem("Start with Windows (elevated)")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled(),
            ToolTipText = "Runs at sign-in as administrator via a scheduled task (no UAC prompt)",
        };
        bool reverting = false;
        startItem.CheckedChanged += (_, _) =>
        {
            if (reverting) return;
            if (!SetAutoStart(startItem.Checked))
            {
                reverting = true;
                startItem.Checked = !startItem.Checked; // creation failed; undo the checkmark
                reverting = false;
                Log("autostart toggle failed");
            }
        };

        var adminItem = new ToolStripMenuItem("Restart as administrator")
        {
            Enabled = !IsElevated(), // no point when already elevated
            ToolTipText = "Restores windows that run elevated (e.g. Task Manager)",
        };
        adminItem.Click += (_, _) => RestartElevated();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Application.Exit();

        menu.Items.Add(startItem);
        menu.Items.Add(adminItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    // Runs on the main thread's message pump; only signals the watcher thread.
    // Filters to events we care about so focus/selection churn doesn't wake it.
    private static void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        switch (eventType)
        {
            case EVENT_OBJECT_CREATE:
            case EVENT_OBJECT_SHOW:
                if (idObject == OBJID_WINDOW)
                {
                    // A window just appeared (CREATE) or became visible (SHOW; windows
                    // created with WS_VISIBLE never fire SHOW). Queue it so the watcher
                    // restores it BEFORE the full snapshot pass (which reads every
                    // window's title cross-process and can take tens of ms on a busy
                    // desktop): a remembered-maximized window then paints maximized
                    // from its first frame instead of flashing normal and snapping.
                    ShowQueue.Enqueue(hwnd);
                    _wakeEvent?.Set();
                }
                break;
            case EVENT_OBJECT_DESTROY:
            case EVENT_OBJECT_HIDE:
                if (idObject == OBJID_WINDOW)
                    _wakeEvent?.Set();
                break;
            case EVENT_OBJECT_STATECHANGE:
                // A maximize just before closing must be observed to record
                // "closed maximized" (idChild carries the new state flags).
                if ((idChild & (STATE_SYSTEM_MAXIMIZED | STATE_SYSTEM_MINIMIZED)) != 0)
                    _wakeEvent?.Set();
                break;
        }
    }

    // Windows broadcasts WM_DISPLAYCHANGE when the display resolution changes or
    // a monitor is plugged/unplugged; it shrinks maximized windows, so wake the
    // watcher to re-maximize them against the new work area.
    private sealed class DisplayChangeFilter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                _displayChanged = true;
                _wakeEvent?.Set();
            }
            return false;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Exit();
        }
        catch (Exception ex)
        {
            Log("restart elevated failed: " + ex.Message); // user declined the UAC prompt
        }
    }

    // Called from the watcher thread when a restore gave up; marshals to the tray.
    private static void NotifyRestoreFailed(string key)
    {
        try
        {
            _notifyIcon?.ShowBalloonTip(8000, "WindowStateKeeper",
                $"Couldn't restore \"{key}\" to maximized. The window is likely running elevated " +
                "(e.g. Task Manager), which Windows hides from non-admin apps. Right-click this icon " +
                "and choose \"Restart as administrator\" to restore it.",
                ToolTipIcon.Warning);
        }
        catch { }
    }

    private static Icon LoadIcon()
    {
        var icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icoPath))
            return new Icon(icoPath);
        var exePath = Environment.ProcessPath;
        if (exePath != null && File.Exists(exePath))
            return Icon.ExtractAssociatedIcon(exePath) ?? throw new InvalidOperationException("no icon in exe");
        throw new InvalidOperationException("icon not found");
    }

    // ---- Auto-start with Windows (elevated, via a logon scheduled task) ----

    private static bool IsAutoStartEnabled()
    {
        if (RunSchTasks("/query", "/tn", AutoStartTaskName))
            return true;
        // legacy: a Run value from before the scheduled-task switch
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string value && value.Length > 0;
    }

    private static bool SetAutoStart(bool enable)
    {
        try
        {
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (exe == null) return false;
                var xmlPath = Path.Combine(Path.GetTempPath(), AutoStartTaskName + "-task.xml");
                // schtasks /create /xml requires UTF-16
                File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);
                try
                {
                    if (!RunSchTasks("/create", "/tn", AutoStartTaskName, "/xml", xmlPath, "/f"))
                        return false;
                }
                finally
                {
                    try { File.Delete(xmlPath); } catch { }
                }
                RemoveRunValue(); // replaced by the scheduled task
            }
            else
            {
                RunSchTasks("/delete", "/tn", AutoStartTaskName, "/f");
                RemoveRunValue();
            }
            return true;
        }
        catch (Exception ex)
        {
            Log("autostart: " + ex.Message);
            return false;
        }
    }

    // Logon trigger, interactive (tray icon visible), highest privileges: starts
    // as administrator at sign-in with no UAC prompt.
    private static string BuildTaskXml(string exePath)
    {
        var cmd = exePath.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
               "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
               "  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>\n" +
               "  <Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType>" +
               "<RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
               "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
               "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
               "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
               "<StartWhenAvailable>false</StartWhenAvailable></Settings>\n" +
               $"  <Actions><Exec><Command>{cmd}</Command></Exec></Actions>\n" +
               "</Task>";
    }

    private static bool RunSchTasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log("schtasks: " + ex.Message);
            return false;
        }
    }

    private static void RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    // ---- Window watcher ----

    private static void WatcherLoop(bool verbose)
    {
        var previous = new Dictionary<IntPtr, WinInfo>();
        // ---- main loop ----
        // (re-launch poller and loop body below, after window-state declarations)
        // key -> when to stop forcing it maximized (a window may fight back during startup)
        var restoreUntil = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // Closes are grouped per key for a few seconds: an app can close several
        // windows at once (e.g. Win11 Notepad owns several visible "Notepad"
        // windows). The window whose state changed most recently decides the
        // group's state (see lastStateAt below).
        var pendingCloses = new Dictionary<string, (bool Maximized, DateTime At, DateTime ChangeAt)>(StringComparer.Ordinal);
        var closeGroupWindow = TimeSpan.FromSeconds(3);
        // A close group is only trusted once it has been quiet this long: an app
        // closes its windows over several passes, and restoring from a partial
        // group would record the wrong state (or miss the user's un-maximize).
        var settleDelay = TimeSpan.FromSeconds(1);
        // When a UWP app is closed, its frame can briefly revert to normal during the
        // teardown animation, so "last visible state" can lie. Remember when each window
        // was last seen maximized and treat a close within this window as maximized.
        var lastMaximizedAt = new Dictionary<IntPtr, DateTime>();
        // Per exe: live process count (refreshed each snapshot) and the time we last saw
        // it spike above baseline. A short-lived 2nd instance (single-instance app
        // re-launch stub that signals the running one then exits) is the signal that the
        // user re-launched the app -> re-maximize its windows that the app just demoted.
        var exeSpikeAt = new Dictionary<string, DateTime>();
        var exeBaseline = new Dictionary<string, int>();
        var knownExes = new HashSet<string>(); // exes seen in the last window snapshot
        // Single-instance re-launch watcher: a re-launch spawns a short-lived 2nd process
        // (stub that signals the running instance then exits) which lives only milliseconds
        // - too brief for the 100ms/2s window poll. A dedicated tight poller samples live
        // process counts for exes we already track; a count spike stamps exeSpikeAt and the
        // main loop re-maximizes the demoted window. A genuine user Restore has no new
        // process, so it is never triggered. ponytail: 50ms poll, only for knownExes; stops
        // with the app (fire-and-forget task).
        var spikeLock = new object();
        var spikeCts = new CancellationTokenSource();
        // Cheap-census state: bare pid buffer, plus a pid -> image-name cache so each
        // process's name is resolved once (not 20x/second). Entries drop when the pid
        // disappears. A protected process whose name can't be read retries each tick -
        // ponytail: OpenProcess-fail retry per tick is microseconds; fine.
        uint[] pids = new uint[1024];
        var pidNames = new Dictionary<uint, string>();
        var livePids = new HashSet<uint>();
        _ = Task.Run(async () =>
        {
            while (!spikeCts.IsCancellationRequested)
            {
                // Idle: no maximized window, so re-launch re-max can't matter. Skip the
                // process census entirely and sleep, letting CPU drop to zero.
                if (!_spikeActive)
                {
                    try { await Task.Delay(50, spikeCts.Token); } catch { break; }
                    continue;
                }
                try
                {
                    // Bare pid list via EnumProcesses: no per-process object or name
                    // resolution. Replaces Process.GetProcesses(), which allocated a
                    // Process per system process and resolved every image name 20x/s.
                    uint count;
                    while (true)
                    {
                        EnumProcesses(pids, pids.Length * sizeof(uint), out uint returned);
                        count = returned / sizeof(uint);
                        if (count < pids.Length) break; // everything fit
                        pids = new uint[pids.Length * 2]; // buffer full: grow and retry
                    }

                    livePids.Clear();
                    for (int i = 0; i < count; i++) livePids.Add(pids[i]);
                    foreach (var dead in pidNames.Keys.Where(k => !livePids.Contains(k)).ToList())
                        pidNames.Remove(dead);
                    for (int i = 0; i < count; i++)
                    {
                        uint pid = pids[i];
                        if (pidNames.ContainsKey(pid)) continue; // name already known
                        string? name = null;
                        var h = OpenProcess(PROC_QUERY_LIMITED_INFO, false, pid);
                        if (h != IntPtr.Zero)
                        {
                            try
                            {
                                var sb = new StringBuilder(512);
                                uint size = (uint)sb.Capacity;
                                if (QueryFullProcessImageName(h, 0, sb, ref size))
                                    name = Path.GetFileNameWithoutExtension(sb.ToString(0, (int)size));
                            }
                            finally { CloseHandle(h); }
                        }
                        if (name != null) pidNames[pid] = name;
                    }

                    foreach (var exe in knownExes.ToList())
                    {
                        string want = Path.GetFileNameWithoutExtension(exe);
                        int live = 0;
                        foreach (var kv in pidNames)
                            if (string.Equals(kv.Value, want, StringComparison.OrdinalIgnoreCase)) live++;
                        lock (spikeLock)
                        {
                            int baseN = exeBaseline.TryGetValue(exe, out var b) ? b : live;
                            if (live > baseN) exeSpikeAt[exe] = DateTime.Now;   // a 2nd instance appeared
                            exeBaseline[exe] = Math.Max(baseN, live);
                        }
                    }
                }
                catch { }
                // ponytail: 50ms sampling (one scan per tick) is ~N*10 fewer table scans
                // than the old per-exe 5ms polls; a re-launch stub shorter than ~50ms may
                // slip through. If re-launch demotes start being missed, drop to 5ms -
                // still one scan per tick, ~30x cheaper than the old per-exe enumeration.
                try { await Task.Delay(50, spikeCts.Token); } catch { break; }
            }
        }, spikeCts.Token);
        // Per window: its last observed state and when it last TRANSITIONED to
        // that state. The window whose state changed most recently is the one
        // the user last touched, so it decides the app's close state (Win11
        // Notepad opens several windows that don't mirror each other: the user
        // un-maximizes one while the others stay maximized).
        var lastStateAt = new Dictionary<IntPtr, (bool Maximized, DateTime At)>();
        // Last known maximized intent per window, from full snapshots only (the
        // fast pass doesn't touch it): used to re-maximize windows that Windows
        // shrinks or restores when the monitor configuration changes.
        var maximizedIntent = new Dictionary<IntPtr, bool>();
        var maximizeHistory = TimeSpan.FromSeconds(2.5);
        // UWP's close animation reverts the frame to normal for ~0.5s before it
        // is destroyed. A close is only "maximized via history" when that final
        // normal state is this brief; a deliberate un-maximize keeps the window
        // normal for longer and must record as a normal close.
        var teardownGrace = TimeSpan.FromSeconds(0.65);
        // After WSK maximizes a window (open-restore or re-assert), the app may
        // revert it to normal (e.g. re-activating an already-open single-instance
        // window drops it to normal). Re-assert maximize within this window so a
        // programmatic revert is undone. ponytail: a genuine user Restore is a
        // sustained normal (handled by the teardownGrace rule); if apps fight
        // longer, raise this.
        var reAssertGrace = TimeSpan.FromSeconds(2.0);
        // A window that has been maximized keeps a "the user wants this maximized"
        // intent until the user explicitly un-maximizes it (the Restore button, or
        // Win+Down on a maximized window) or the window closes. It drives RESTORE
        // decisions: re-activating a minimized window (or opening a sibling) comes
        // back maximized no matter how long ago the maximize was. Separate from
        // lastMaximizedAt (the close-recording grace), so a deliberate
        // un-maximize + close still records normal.
        var intendedMaximized = new HashSet<IntPtr>();
        // When each window was last maximized BY WSK (open-restore or re-assert):
        // used to re-assert maximize if the app reverts a freshly-maximized window
        // to normal (single-instance re-activation), without fighting a real user Restore.
        var lastMaxAssert = new Dictionary<IntPtr, DateTime>();
        // Sustained (not transient) un-maximize per window: a maximized window can
        // momentarily read "normal" during DWM/animation; only trust the un-maximize
        // once it has stayed normal past teardownGrace, so a blip can't corrupt the
        // recorded close state. Set on first non-max reading, cleared when it returns
        // to max or the window closes.
        var unMaximizedSince = new Dictionary<IntPtr, DateTime>();
        // Last showCmd per window: detects the minimized -> normal transition that
        // means "the window was re-activated", and the maximized -> normal one that
        // means "the user clicked Restore" (which ends the intent).
        var lastShowCmd = new Dictionary<IntPtr, int>();
        // When each window was first seen. A title CHANGE only re-evaluates the
        // restore while the window is young (Explorer first shows a generic "File
        // Explorer" title, then the folder name). Later title changes are browser
        // tab switches: re-evaluating then would re-maximize a window the user may
        // have already un-maximized.
        var firstSeenAt = new Dictionary<IntPtr, DateTime>();
        // When each window was last close-detected: a window that reappears within
        // reopenGrace (tray re-show, quick restart) is re-evaluated on every pass
        // until it restores, instead of only on its first sight (see the restore
        // block). Entries expire on their own; pruned with the hwndInstance sweep.
        var closedAt = new Dictionary<IntPtr, DateTime>();
        // A window first sighted maximized after an absence (the app restored its
        // own saved state) may self-demote to normal right after showing (AHK tray
        // double-click). Track when it reappeared maximized so the restore block
        // can re-maximize it if that happens within reAppearGrace.
        var reAppearMax = new Dictionary<IntPtr, DateTime>();
        var titleSettleWindow = TimeSpan.FromSeconds(5);
        // How long after a window was close-detected a reappearance is still
        // re-evaluated for restore (spans the close-record flush delay, so a tray
        // re-show or fast restart right after closing is never missed).
        var reopenGrace = TimeSpan.FromSeconds(5);
        // How long after a window reappeared maximized a self-demote is still
        // re-maximized. ponytail: 2s fixed; long enough for the sub-second AHK
        // demote, short enough to not fight a user un-maximizing after reopen.
        var reAppearGrace = TimeSpan.FromSeconds(2.0);
        // Full-pass cadence while any window is recently closed: some tray-icon
        // apps re-show their window without a SHOW event reaching us (AHK
        // double-click), so the restore comes from the next full pass. Poll at
        // 250ms then, ~2s otherwise, so a re-showed window is maximized in a
        // quarter second instead of flashing normal for up to 2s.
        var ReopenPollMs = 250;
        // Windows queued by the WinEvent callback that could not be decided yet
        // (not visible, or their title is still settling into a recorded key).
        // Re-checked every iteration until they resolve or age out.
        var restoreCandidates = new HashSet<IntPtr>();
        var candidateSince = new Dictionary<IntPtr, DateTime>();
        // Per hwnd: the (base key, instance index) assigned at first sight, kept until
        // the window closes or its base key changes (title settle). See WinInfo.Instance.
        var hwndInstance = new Dictionary<IntPtr, (string BaseKey, int Instance)>();
        int tick = 0;
        var nextFull = DateTime.MinValue;

        while (true)
        {
            try
            {
                // Fast targeted pass: while any window has a maximized intent, watch
                // its placement at 100ms so an un-maximize is observed promptly and a
                // minimized->normal re-activation of an intended window is caught.
                bool fast = lastMaximizedAt.Count > 0 || intendedMaximized.Count > 0;
                // Re-launch re-max only matters while a maximized window exists, so let
                // the 50ms poller sleep otherwise. Mirrors the same trigger as `fast`.
                _spikeActive = fast;
                // While any young window is undecided, iterate at the fast rate so its
                // title settle / visibility race resolves promptly.
                bool signaled = _wakeEvent?.WaitOne((fast || restoreCandidates.Count > 0 || closedAt.Count > 0 || reAppearMax.Count > 0) ? FastPollMs : FallbackPollMs) ?? false;
                bool fullDue = DateTime.Now >= nextFull;

                // Immediate restore path: windows that just appeared are restored
                // here, BEFORE the full snapshot pass, so they paint maximized from the
                // first frame. A CREATE event fires while the window is still being
                // created (WS_VISIBLE applied right after) and titles can settle after
                // show, so an undecided window becomes a candidate and is re-checked
                // each iteration until it resolves or ages out (mirrors the full
                // pass's title-settle window, but targeted at just these windows so it
                // stays fast). A window that stays hidden (a deliberate hidden helper)
                // is never touched.
                while (ShowQueue.TryDequeue(out var shown))
                {
                    if (!candidateSince.ContainsKey(shown))
                        candidateSince[shown] = DateTime.Now;
                    if (TryRestoreNow(shown, pendingCloses))
                        restoreCandidates.Add(shown);
                    else
                        restoreCandidates.Remove(shown);
                }
                foreach (var hwnd in restoreCandidates.ToList())
                {
                    if (DateTime.Now - candidateSince[hwnd] > titleSettleWindow)
                    { restoreCandidates.Remove(hwnd); continue; }
                    if (TryRestoreNow(hwnd, pendingCloses))
                        restoreCandidates.Add(hwnd);
                    else
                        restoreCandidates.Remove(hwnd);
                }

                // Single-instance re-launch detection. A re-launch spawns a short-lived 2nd
                // process (the stub that signals the running instance then exits); the tight
                // poller above stamps exeSpikeAt when it sees the count spike. Here we act on a
                // recent spike: re-maximize the exe's windows that the app just demoted to
                // normal. A genuine user Restore has no new process, so it is never triggered.
                // ponytail: relaunchGrace 2s covers the ~0.3s demote after the stub exits.
                var relaunchGrace = TimeSpan.FromSeconds(2.0);
                foreach (var exe in knownExes)
                {
                    if (exeSpikeAt.TryGetValue(exe, out var spiked) && DateTime.Now - spiked <= relaunchGrace)
                    {
                        foreach (var (hwnd, info) in previous)
                        {
                            if (info.Exe != exe) continue;
                            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                            if (!GetWindowPlacement(hwnd, ref placement)) continue;
                            if (placement.showCmd == SW_SHOWMAXIMIZED) continue;
                            // Re-max if the window was supposed to be maximized: a closed-
                            // maximized record, an active maximize intent, or WSK having seen
                            // it maximized recently (covers a user- or WSK-maximized window
                            // that the re-launch demoted).
                            bool wasMax = WasClosedMaximized(info.Key) || intendedMaximized.Contains(hwnd)
                                || (maximizedIntent.TryGetValue(hwnd, out var mi) && mi)
                                || (lastMaximizedAt.TryGetValue(hwnd, out var lm) && DateTime.Now - lm <= relaunchGrace);
                            if (wasMax)
                            {
                                lastMaxAssert[hwnd] = DateTime.Now;
                                Log($"re-assert {hwnd:X} {info.Key} (re-launch demoted a maximized window)");
                                ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                            }
                        }
                    }
                }
                foreach (var exe in exeSpikeAt.Keys.ToList())
                    if (DateTime.Now - exeSpikeAt[exe] > relaunchGrace) exeSpikeAt.Remove(exe);

                if (fast && !signaled && !fullDue)
                {
                    foreach (var hwnd in lastMaximizedAt.Keys.Union(intendedMaximized).ToList())
                    {
                        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                        if (!GetWindowPlacement(hwnd, ref placement)) continue; // gone; the full pass records the close
                        bool maximizedNow = placement.showCmd == SW_SHOWMAXIMIZED ||
                            (placement.showCmd == SW_SHOWMINIMIZED && (placement.flags & WPF_RESTORETOMAXIMIZED) != 0);
                        // A maximized window reading "normal" for a single fast tick is usually a
                        // transient (DWM/animation), not a real un-maximize. Track WHEN the non-max
                        // state began; only a sustained one (past teardownGrace) is a genuine
                        // un-maximize. ponytail: 0.65s sustained rule; a blip that returns to max is cancelled.
                        if (maximizedNow)
                        {
                            unMaximizedSince.Remove(hwnd);
                            lastStateAt[hwnd] = (true, DateTime.Now);
                        }
                        else if (!unMaximizedSince.TryGetValue(hwnd, out var since))
                            unMaximizedSince[hwnd] = DateTime.Now;
                        else if (DateTime.Now - since > teardownGrace)
                        {
                            lastStateAt[hwnd] = (false, since); // commit the un-maximize at its start
                            intendedMaximized.Remove(hwnd);
                            unMaximizedSince.Remove(hwnd);
                        }
                        // Re-assert maximize ONLY when a re-launch spike (a 2nd short-lived
                        // process) explains the revert - that's the single-instance app
                        // demoting its own window. A genuine user Restore has no spike, so it
                        // is never re-maximized. lastMaxAssert covers WSK-initiated maximizes.
                        else if ((lastMaxAssert.TryGetValue(hwnd, out var asserted) && DateTime.Now - asserted <= reAssertGrace)
                              || (lastMaximizedAt.TryGetValue(hwnd, out var lastMax) && DateTime.Now - lastMax <= reAssertGrace
                                  && previous.TryGetValue(hwnd, out var pinf) && exeSpikeAt.TryGetValue(pinf.Exe, out var spiked) && DateTime.Now - spiked <= relaunchGrace))
                        {
                            lastMaxAssert[hwnd] = DateTime.Now;
                            Log($"re-assert {hwnd:X} (app reverted a freshly-maximized window)");
                            ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                        }
                        if (lastShowCmd.TryGetValue(hwnd, out var prevCmd))
                        {
                            if (prevCmd == SW_SHOWMAXIMIZED && placement.showCmd == SW_SHOWNORMAL
                                && !unMaximizedSince.ContainsKey(hwnd))
                                intendedMaximized.Remove(hwnd); // user clicked Restore: intent ends
                            else if (prevCmd == SW_SHOWMINIMIZED && placement.showCmd == SW_SHOWNORMAL
                                && intendedMaximized.Contains(hwnd))
                            {
                                // Re-activated a minimized window that should stay maximized
                                // (e.g. the desktop double-click restoring the existing
                                // folder window to normal): re-maximize it.
                                Log($"restored {hwnd:X} (minimized->normal, intent maximized)");
                                lastMaxAssert[hwnd] = DateTime.Now;
                                ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                            }
                        }
                        lastShowCmd[hwnd] = placement.showCmd;
                    }
                    continue;
                }

                var current = Snapshot(previous, hwndInstance);

                // Monitor configuration changed (resolution change, monitor
                // plugged/unplugged): Windows shrinks maximized windows or restores
                // them to normal. Re-apply maximized to every window whose last
                // known intent was maximized so they fill the new work area.
                if (_displayChanged)
                {
                    _displayChanged = false;
                    int reMaxed = 0;
                    foreach (var (hwnd, info) in current)
                    {
                        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                        if (!GetWindowPlacement(hwnd, ref placement)) continue;
                        if (placement.showCmd == SW_SHOWMINIMIZED) continue; // don't yank minimized windows up
                        if (placement.showCmd == SW_SHOWMAXIMIZED
                            || (maximizedIntent.TryGetValue(hwnd, out var intent) && intent))
                        {
                            ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                            reMaxed++;
                        }
                    }
                    if (reMaxed > 0)
                        Log($"display change: re-maximized {reMaxed} window(s)");
                }

                if (verbose)
                {
                    foreach (var (hwnd, info) in current)
                        Log($"  open  {hwnd:X} pid={info.Pid} {info.Key} max={info.Maximized}");
                }

                // Detect windows that closed; stage their state for grouping.
                foreach (var (hwnd, info) in previous)
                {
                    if (!current.ContainsKey(hwnd))
                    {
                        // The window the user last touched (latest state change)
                        // decides the close state, not "any maximized wins".
                        bool wasMaximized = info.Maximized;
                        DateTime changeAt = DateTime.MinValue;
                        if (lastStateAt.TryGetValue(hwnd, out var st))
                        {
                            wasMaximized = st.Maximized;
                            changeAt = st.At;
                        }
                        // A maximized window that has been continuously non-maximized up to
                        // the moment of close (past teardownGrace) really is un-maximized; a
                        // brief blip that returned to max left no sustained entry and is
                        // ignored, so it still restores maximized next time.
                        if (unMaximizedSince.TryGetValue(hwnd, out var um) && DateTime.Now - um > teardownGrace)
                            wasMaximized = false;
                        // Last seen normal but recently maximized: only trust the
                        // maximize if the final normal state is brief (UWP close
                        // animation reverts for ~0.5s); a deliberate un-maximize
                        // keeps the window normal longer and is a normal close.
                        else if (!wasMaximized && lastMaximizedAt.TryGetValue(hwnd, out var lastMax)
                            && DateTime.Now - lastMax <= maximizeHistory
                            && DateTime.Now - changeAt <= teardownGrace)
                            wasMaximized = true;
                        lastMaximizedAt.Remove(hwnd);
                        lastStateAt.Remove(hwnd);
                        maximizedIntent.Remove(hwnd);
                        intendedMaximized.Remove(hwnd);
                        lastShowCmd.Remove(hwnd);
                        firstSeenAt.Remove(hwnd);
                        unMaximizedSince.Remove(hwnd);
                        closedAt[hwnd] = DateTime.Now;
                        if (pendingCloses.TryGetValue(info.Key, out var pc))
                        {
                            if (changeAt > pc.ChangeAt)
                                pendingCloses[info.Key] = (wasMaximized, DateTime.Now, changeAt);
                            else
                                pendingCloses[info.Key] = (pc.Maximized, DateTime.Now, pc.ChangeAt);
                        }
                        else
                            pendingCloses[info.Key] = (wasMaximized, DateTime.Now, changeAt);
                        restoreUntil.Remove(info.Key);
                    }
                }

                // Flush close groups that stopped receiving new closes.
                foreach (var (key, pc) in pendingCloses.ToList())
                {
                    if (DateTime.Now - pc.At >= closeGroupWindow)
                    {
                        pendingCloses.Remove(key);
                        RecordClosed(key, pc.Maximized);
                    }
                }

                // Track when each window was last seen maximized (see maximizeHistory above).
                // Also remember which exes are live, so the per-iteration re-launch detector
                // (above) only refreshes process counts for exes we already track.
                foreach (var (hwnd, info) in current)
                {
                    knownExes.Add(info.Exe);
                    maximizedIntent[hwnd] = info.Maximized;
                    if (info.Maximized)
                    {
                        lastMaximizedAt[hwnd] = DateTime.Now;
                        intendedMaximized.Add(hwnd);
                        unMaximizedSince.Remove(hwnd);
                        lastStateAt[hwnd] = (true, DateTime.Now);
                    }
                    else
                    {
                        // Treat a non-max reading as a real un-maximize only once it has
                        // lasted past teardownGrace; the fast pass already times it via
                        // unMaximizedSince, and a single snapshot blip is ignored here.
                        if (!unMaximizedSince.TryGetValue(hwnd, out var since))
                            unMaximizedSince[hwnd] = DateTime.Now;
                        else if (DateTime.Now - since > teardownGrace)
                        {
                            lastStateAt[hwnd] = (false, since);
                            intendedMaximized.Remove(hwnd);
                        }
                        if (lastMaximizedAt.TryGetValue(hwnd, out var lastMax) && DateTime.Now - lastMax > maximizeHistory)
                            lastMaximizedAt.Remove(hwnd);
                    }

                    if (lastShowCmd.TryGetValue(hwnd, out var prevCmd))
                    {
                        if (prevCmd == SW_SHOWMAXIMIZED && info.ShowCmd == SW_SHOWNORMAL
                            && !unMaximizedSince.ContainsKey(hwnd))
                            intendedMaximized.Remove(hwnd); // user clicked Restore: intent ends
                        else if (prevCmd == SW_SHOWMINIMIZED && info.ShowCmd == SW_SHOWNORMAL
                            && intendedMaximized.Contains(hwnd))
                        {
                            // Re-activated a minimized window that should stay maximized:
                            // fallback path when the fast pass didn't run.
                            Log($"restored {hwnd:X} (minimized->normal, intent maximized)");
                            lastMaxAssert[hwnd] = DateTime.Now;
                            ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                        }
                    }
                    lastShowCmd[hwnd] = info.ShowCmd;
                    if (!firstSeenAt.ContainsKey(hwnd))
                        firstSeenAt[hwnd] = DateTime.Now;
                }

                // Detect windows that opened; restore them if their key was closed maximized.
                foreach (var (hwnd, info) in current)
                {
                    // Flush a pending close for this key now, so a quick restart is
                    // restored too — but only once the group has settled. A partial
                    // group (multi-window apps close over several passes) would
                    // record the wrong state.
                    if (pendingCloses.TryGetValue(info.Key, out var pc))
                    {
                        if (DateTime.Now - pc.At >= settleDelay)
                        {
                            pendingCloses.Remove(info.Key);
                            RecordClosed(info.Key, pc.Maximized);
                            if (pc.Maximized)
                                restoreUntil[info.Key] = DateTime.Now + RestoreGiveUp; // quick restart: restore now
                        }
                        else
                            continue; // still settling: don't restore on a stale record yet
                    }
                    if (info.Maximized)
                    {
                        restoreUntil.Remove(info.Key); // already maximized, nothing to do
                        if (!previous.ContainsKey(hwnd))
                            reAppearMax[hwnd] = DateTime.Now; // reappeared max; watch for self-demote
                        continue;
                    }
                    if (restoreUntil.TryGetValue(info.Key, out var giveUp))
                    {
                        if (DateTime.Now >= giveUp)
                        {
                            restoreUntil.Remove(info.Key); // gave up; app refuses to be maximized
                            Log($"restore failed for {info.Key} (window never became maximized; may run elevated)");
                            NotifyRestoreFailed(info.Key);
                            continue;
                        }
                        // If it was maximized at any point since opening (the user may
                        // have since un-maximized it), the restore already worked: stop.
                        if (lastMaximizedAt.ContainsKey(hwnd))
                        {
                            restoreUntil.Remove(info.Key);
                            continue;
                        }
                        lastMaxAssert[hwnd] = DateTime.Now;
                        ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                    }
                    // Restore only when the window just opened (isNew): once the user
                    // un-maximizes an already-open window we must not fight them.
                    // Also restore when a sibling of the same key is currently maximized
                    // (or minimized from maximized): the close-record only knows windows
                    // that closed, but a minimized sibling never closed (maximize a
                    // folder, minimize it, reopen it -> the new window should still
                    // come back maximized).
                    // A window whose key CHANGED since the last pass (Explorer first
                    // shows a generic "File Explorer" title, then the folder name)
                    // is re-evaluated against the new key: the first observation was
                    // under the wrong key, so the restore must not have been missed.
                    // Only while the title is still settling, though: a later title
                    // change is a browser tab switch, and re-evaluating then would
                    // re-maximize a window the user may have already un-maximized.
                    // A window close-detected within reopenGrace (a tray-icon app
                    // re-showing its hidden window, or any re-open) is re-evaluated
                    // on every pass until it restores: the one-shot first-sight
                    // restore can miss the exact reappearance (record not flushed
                    // yet, cloak at show, dropped event) and would otherwise leave
                    // the window normal forever despite the record saying maximized.
                    else if ((!previous.ContainsKey(hwnd)
                            || (previous[hwnd].Key != info.Key && firstSeenAt.TryGetValue(hwnd, out var firstSeen)
                                && DateTime.Now - firstSeen <= titleSettleWindow)
                            || (closedAt.TryGetValue(hwnd, out var closedWhen) && DateTime.Now - closedWhen <= reopenGrace
                                && !(lastMaxAssert.TryGetValue(hwnd, out var lastRestored) && DateTime.Now - lastRestored <= reAssertGrace))
                            // A window that reappeared maximized then self-demoted
                            // (AHK-style tray re-show) is re-maximized while within
                            // reAppearGrace of that first sighting, so the one-shot
                            // first-sight restore isn't consumed by the already-max
                            // pass that follows the app's own state restore.
                            || (reAppearMax.TryGetValue(hwnd, out var reappeared) && DateTime.Now - reappeared <= reAppearGrace
                                && !(lastMaxAssert.TryGetValue(hwnd, out var lastRestored2) && DateTime.Now - lastRestored2 <= reAssertGrace)))
                        && (WasClosedMaximized(info.Key)
                            || current.Any(kv => kv.Key != hwnd && kv.Value.BaseKey == info.BaseKey
                                && (kv.Value.Maximized
                                    // A sibling the user has maximized (and not since
                                    // restored) still means "this window wants to be
                                    // maximized" even if it's currently minimized.
                                    || intendedMaximized.Contains(kv.Key)))))
                    {
                        restoreUntil[info.Key] = DateTime.Now + RestoreGiveUp;
                        Log($"restored {hwnd:X} {info.Key}");
                        lastMaxAssert[hwnd] = DateTime.Now;
                        ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                    }
                }

                previous = current;

                // Free instance slots for windows that vanished without a full pass
                // (created and destroyed between polls): a burned slot would drift the
                // index upward forever. Windows in `current` keep their assignment.
                foreach (var h in hwndInstance.Keys.ToList())
                    if (!current.ContainsKey(h))
                        hwndInstance.Remove(h);
                foreach (var h in closedAt.Keys.ToList())
                    if (DateTime.Now - closedAt[h] > reopenGrace)
                        closedAt.Remove(h);
                foreach (var h in reAppearMax.Keys.ToList())
                    if (DateTime.Now - reAppearMax[h] > reAppearGrace)
                        reAppearMax.Remove(h);

                // Schedule the next full pass here (not at the top): the counts for
                // closedAt/reAppearMax updated during THIS pass must decide the
                // cadence of the NEXT pass, so a window that just reappeared (max or
                // recently closed) is re-polled in a quarter second, not up to 2s.
                nextFull = DateTime.Now.AddMilliseconds(
                    (closedAt.Count > 0 || reAppearMax.Count > 0) ? ReopenPollMs : FallbackPollMs);

                // Routine heartbeat: only in --verbose mode, so the log stays
                // event-only (and small) during normal use.
                if (++tick % 30 == 0 && verbose)
                    Log($"tick windows={current.Count} restorePending={restoreUntil.Count}");
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Fast-path restore for a window that just appeared (see the queue drain at
        // the top of the loop). Mirrors the Snapshot filter and restores straight
        // from the record, so the ShowWindow happens milliseconds after the window
        // was created instead of after a full pass. Returns true when the window
        // should keep being watched (not visible yet, or its title may still be
        // settling into a recorded key); false when decided (restored, or skipped
        // for good). Local function: reads pendingCloses on the same thread, so no
        // locking is needed.
        bool TryRestoreNow(IntPtr hwnd, Dictionary<string, (bool Maximized, DateTime At, DateTime ChangeAt)> pending)
        {
            try
            {
                if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == (uint)Environment.ProcessId)
                    return false;
                if (!IsWindowVisible(hwnd))
                    return true; // not visible yet: keep watching
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;       // owned window (dialog, popup)
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true; // cloaked at show is transient (tray pop-out, animation): keep watching

                var cls = GetText(hwnd, GetClassName);
                if (cls.Length == 0 || IgnoredClasses.Contains(cls)) return false;

                var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(hwnd, ref placement)) return false;
                bool maximized = placement.showCmd == SW_SHOWMAXIMIZED ||
                    (placement.showCmd == SW_SHOWMINIMIZED && (placement.flags & WPF_RESTORETOMAXIMIZED) != 0);
                if (maximized)
                {
                    reAppearMax[hwnd] = DateTime.Now; // app restored its own state; watch for self-demote
                    return false;
                }

                var exe = GetProcessName(pid);
                var title = GetWindowTextSafe(hwnd);
                // An empty title may be a transient read failure (busy thread) on a
                // window that will get a title, or a genuinely untitled window. Either
                // way, don't decide on the shared exe|class key here: the full pass
                // re-evaluates once the title settles (or restores untitled windows
                // from their class record). This stops a not-yet-titled window from
                // matching a stale record left under exe|class by another window.
                if (title.Length == 0)
                    return true;
                var key = new WinInfo
                {
                    Pid = pid,
                    Exe = exe,
                    Class = cls,
                    Title = title,
                    Maximized = false,
                    ShowCmd = placement.showCmd,
                    Instance = AssignInstance(hwnd, BuildBaseKey(exe, cls, title), hwndInstance),
                }.Key;
                // A close group may still be settling (the record is written only
                // once it settles). Trust the group's own staged state - the same
                // signal the full pass will eventually flush - so a quick restart
                // after closing maximized is restored immediately, while a quick
                // restart after closing NORMAL is correctly left alone (using the
                // stale record here would re-introduce the stuck-record bug).
                bool shouldRestore;
                if (pending.TryGetValue(key, out var pc))
                    shouldRestore = pc.Maximized;
                else
                {
                    var rec = RecordState(key);
                    if (rec < 0) return true; // no record yet: the title may still settle into a recorded key
                    shouldRestore = rec == 1;
                }
                if (!shouldRestore) return false;
                Log($"restored {hwnd:X} {key} (immediate)");
                lastMaxAssert[hwnd] = DateTime.Now;
                ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                return false;
            }
            catch
            {
                return false; // the fast path must never take the watcher down
            }
        }
    }

    // Assigns the per-window instance index: reuse the window's own assignment while
    // its base key is unchanged (stable for its lifetime), else the smallest free
    // index among live siblings of the same base key. Instance 1 = plain key (the
    // primary window); siblings get |#2, |#3, ... so two same-title windows keep
    // separate records. A closed window's slot is freed by the caller pruning
    // hwndInstance, so a reopened window reclaims it and finds its own record.
    private static int AssignInstance(IntPtr hwnd, string baseKey,
        Dictionary<IntPtr, (string BaseKey, int Instance)> hwndInstance)
    {
        if (hwndInstance.TryGetValue(hwnd, out var cur) && cur.BaseKey == baseKey)
            return cur.Instance;
        var taken = new HashSet<int>();
        foreach (var (h, a) in hwndInstance)
            if (h != hwnd && a.BaseKey == baseKey) taken.Add(a.Instance);
        int idx = 1;
        while (taken.Contains(idx)) idx++;
        hwndInstance[hwnd] = (baseKey, idx);
        return idx;
    }

    private static Dictionary<IntPtr, WinInfo> Snapshot(Dictionary<IntPtr, WinInfo> previous,
        Dictionary<IntPtr, (string BaseKey, int Instance)> hwndInstance)
    {
        var result = new Dictionary<IntPtr, WinInfo>();
        uint selfPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == selfPid)
                return true;
            if (!IsWindowVisible(hwnd))
                return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) // owned window (dialog, popup)
                return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true; // cloaked (e.g. minimized/suspended UWP app, virtual desktop)

            var cls = GetText(hwnd, GetClassName);
            if (cls.Length == 0 || IgnoredClasses.Contains(cls))
                return true;

            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            bool maximized = false;
            int showCmd = 0;
            if (GetWindowPlacement(hwnd, ref placement))
            {
                showCmd = placement.showCmd;
                maximized = placement.showCmd == SW_SHOWMAXIMIZED ||
                            (placement.showCmd == SW_SHOWMINIMIZED && (placement.flags & WPF_RESTORETOMAXIMIZED) != 0);
            }

            // Reuse the exe name for windows we already track: the poll shouldn't
            // open a Process handle per window per pass.
            var exe = previous.TryGetValue(hwnd, out var prev) && prev.Pid == pid ? prev.Exe : GetProcessName(pid);
            var title = GetWindowTextSafe(hwnd);
            // A transient empty title read (SendMessageTimeout timing out on a busy
            // thread) must not collapse the window onto the shared exe|class key: that
            // would merge it with unrelated windows (and their records). Keep the last
            // non-empty title we saw for this window instead.
            if (title.Length == 0 && prev != null && prev.Pid == pid && prev.Title.Length > 0)
                title = prev.Title;
            result[hwnd] = new WinInfo
            {
                Pid = pid,
                Exe = exe,
                Class = cls,
                Title = title,
                Maximized = maximized,
                ShowCmd = showCmd,
                Instance = AssignInstance(hwnd, BuildBaseKey(exe, cls, title), hwndInstance),
            };
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return "unknown.exe"; // process already gone or not accessible
        }
    }

    private static string GetText(IntPtr hwnd, Func<IntPtr, StringBuilder, int, int> getter)
    {
        var sb = new StringBuilder(256);
        getter(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // Bounded replacement for GetWindowText: never blocks on a hung window.
    private static string GetWindowTextSafe(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        SendMessageTimeout(hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb, SMTO_ABORTIFHUNG, TextTimeoutMs, out _);
        return sb.ToString();
    }

    private static string RegValueName(string key)
    {
        // Registry value names cannot contain backslashes; use a stable hex digest.
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    // 1 = record says maximized, 0 = record says normal, -1 = no record.
    private static int RecordState(string key)
    {
        using var keyHandle = Registry.CurrentUser.OpenSubKey(RegRoot);
        return keyHandle?.GetValue(RegValueName(key)) is int v ? v : -1;
    }

    private static bool WasClosedMaximized(string key)
    {
        return RecordState(key) == 1;
    }

    private static void RecordClosed(string key, bool maximized)
    {
        using var keyHandle = Registry.CurrentUser.CreateSubKey(RegRoot);
        keyHandle.SetValue(RegValueName(key), maximized ? 1 : 0, RegistryValueKind.DWord);
        Log($"closed {key} ({(maximized ? "maximized" : "normal")})");
    }

    // ---- Logging ----

    private static readonly object LogLock = new();
    private static string? _logPath;

    private static void Log(string line)
    {
        try
        {
            lock (LogLock)
            {
                _logPath ??= ResolveLogPath();
                if (_logPath != null)
                {
                    // Size cap: past LogCapBytes keep only the newer half, aligned to a
                    // line break. Routine logging is event-only now, so this runs rarely.
                    var fi = new FileInfo(_logPath);
                    if (fi.Exists && fi.Length > LogCapBytes)
                    {
                        var text = File.ReadAllText(_logPath);
                        int cut = text.IndexOf('\n', text.Length / 2);
                        if (cut > 0) text = text[(cut + 1)..];
                        File.WriteAllText(_logPath, text);
                    }
                    File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
                }
            }
        }
        catch
        {
            // logging must never take the watcher down
        }
    }

    private const long LogCapBytes = 2 * 1024 * 1024;

    private static string? ResolveLogPath()
    {
        // %LOCALAPPDATA% first (the standard place for per-user app data); the exe
        // folder is only a fallback for locked-down installs.
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    AppContext.BaseDirectory })
        {
            try
            {
                var path = Path.Combine(dir, "WindowStateKeeper.log");
                File.AppendAllText(path, "");
                return path;
            }
            catch
            {
                // try the next location
            }
        }
        return null;
    }
}
