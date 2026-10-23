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

// WindowMaximizer: remembers whether each top-level window was closed
// maximized, and re-maximizes it the next time it opens; windows closed
// normal reopen with their saved position/size. Works for classic
// Win32 windows and UWP apps (ApplicationFrameHost frame windows).
//
// Runs in the system tray. Right-click the icon for the menu: "Start with
// Windows (elevated)" (creates/removes a logon scheduled task) and "Exit".
// The app always runs elevated (requireAdministrator in app.manifest).
//
// State is stored per window "key" in HKCU\Software\WindowMaximizer.
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
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int WM_SETTINGCHANGE = 0x001A;

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
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
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

    private const string RegRoot = @"Software\WindowMaximizer";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WindowMaximizer";
    private const string AutoStartTaskName = "WindowMaximizer";
    private const string UwpFrameExe = "ApplicationFrameHost.exe";
    private static readonly TimeSpan RestoreGiveUp = TimeSpan.FromSeconds(15);
    // Safety-net poll cadence. Detection is event-driven (SetWinEventHook wakes the
    // watcher the moment a window opens/closes or maximizes), so this only covers
    // events WinEvent may miss under load.
    private const int FallbackPollMs = 2000;
    // While a window was recently maximized we watch its placement at this rate:
    // no WinEvent fires for maximize/un-maximize on Windows 11, so a prompt poll
    // is the only way to observe a deliberate un-maximize (and separate it from
    // a UWP close-animation revert). 200ms halves the fast-pass CPU vs. 100ms;
    // all grace rules are time-based, so only detection latency changes (+100ms).
    private const int FastPollMs = 200;

    private sealed class WinInfo
    {
        public required uint Pid;
        public required string Exe;
        public required string Class;
        public required string Title;
        public required bool Maximized;
        public required int ShowCmd;
        public required RECT NormalBounds;
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

    // ponytail: compiled once; the pattern is fixed-length so this avoids re-parsing every call.
    private static readonly Regex WpfGuidSuffix = new(@";[0-9a-fA-F-]{36}", RegexOptions.Compiled);

    private static string BuildBaseKey(string exe, string cls, string title) =>
        string.IsNullOrEmpty(title)
            ? $"{exe}|{WpfGuidSuffix.Replace(cls, "")}"
            : $"{exe}|{WpfGuidSuffix.Replace(cls, "")}|{title}";

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Shell_DwmTabWindow",
    };

    // Standard Win32 dialogs build their children asynchronously during
    // WM_INITDIALOG (e.g. cleanmgr's "Disk Cleanup for (C:)" populates its file
    // list while scanning). Resizing one mid-init breaks child creation and
    // leaves the dialog blank, so dialogs get a move-only position restore
    // (no WM_SIZE, no re-layout) instead of a full bounds restore. The
    // maximized path still applies (dialogs are almost never maximized, and
    // a maximized record on one means the user explicitly put it there).
    private static bool IsDialogClass(string cls) =>
        cls.Equals("#32770", StringComparison.Ordinal);

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
    private static uint _wmTaskbarCreated;
    private static Form? _hiddenForm;
    private static TaskbarNativeWindow? _msgWindow;

    [STAThread]
    private static int Main(string[] args)
    {
        // Windows 11 Fluent baseline: visual styles + per-monitor DPI so the
        // tray menu renders with the modern theme, Segoe UI Variable, and
        // crisp scaling. Must run before any handle is created.
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
        try { Application.EnableVisualStyles(); } catch { }
        try { Application.SetCompatibleTextRenderingDefault(false); } catch { }

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
        _instanceMutex = new Mutex(true, @"Local\WindowMaximizer.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            Log("second instance detected; exiting");
            return 0;
        }

        // Self-heal a stale scheduled task left from before the power-settings fix
        // (StopIfGoingOnBatteries=true kills the task-launched instance on sleep/wake).
        try { EnsureAutoStartTaskHealthy(); } catch (Exception ex) { Log("task heal check failed: " + ex.Message); }

        // Register for TaskbarCreated (Explorer restart) so the tray icon is
        // recreated instead of silently vanishing while the process keeps running.
        try { _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated"); } catch { }

        // Event-driven detection: wake the watcher the moment a window is created,
        // shown, hidden, destroyed, or toggles maximize/minimize. The callback runs
        // on this (message-pumping) thread and just signals the watcher thread, so
        // all window logic stays on one thread. The fallback poll covers gaps.
        _wakeEvent = new AutoResetEvent(false);
        _winEventProc = WinEventProc;
        _winEventHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_NAMECHANGE,
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
            try { _regRead?.Dispose(); } catch { }
            _regRead = null;
            try { _hiddenForm?.Dispose(); } catch { }
            try { _msgWindow?.DestroyHandle(); } catch { }
        };

        // SystemEvents needs a pump (we have one via Application.Run). Register
        // before any Control is created (same restriction as SetUnhandledExceptionMode).
        try
        {
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                Log($"power {e.Mode}");
                if (e.Mode == PowerModes.Resume)
                {
                    _wakeEvent?.Set();
                    _displayChanged = true;
                    try { RehookWinEvent(); } catch { }
                    try { RecreateTrayIcon(); } catch (Exception ex) { Log("resume tray recreate failed: " + ex.Message); }
                }
                else if (e.Mode == PowerModes.Suspend)
                {
                    Log("power suspend");
                }
            };
            SystemEvents.SessionEnding += (s, e) => Log($"session ending {e.Reason}");
            SystemEvents.SessionSwitch += (s, e) => Log($"session switch {e.Reason}");
            // Fires when the Windows light/dark mode changes (ImmersiveColorSet).
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General)
                {
                    try { OnSystemThemeMaybeChanged(); } catch { }
                }
            };
        }
        catch (Exception ex) { Log("SystemEvents hook failed: " + ex.Message); }

        // Hidden form keeps a top-level window handle on this thread so
        // TaskbarCreated broadcasts are delivered, and provides an Invoke
        // target for cross-thread tray recreation (e.g. from SystemEvents).
        _hiddenForm = new TaskbarForm();
        // Force handle creation without showing
        var _h = _hiddenForm.Handle;
        _msgWindow = new TaskbarNativeWindow();
        Log($"msg windows created form={_h:X} native={_msgWindow.Handle:X} taskbarMsg={_wmTaskbarCreated}");

        try
        {
            CreateTrayIcon();
        }
        catch (Exception ex)
        {
            Log("tray init failed: " + ex);
            MessageBox.Show("WindowMaximizer could not start its tray icon:\n" + ex.Message,
                "WindowMaximizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        // Theme submenu: System (follow Windows) is the default; Light/Dark override.
        var themeItem = new ToolStripMenuItem("Theme")
        {
            ToolTipText = "System follows Windows Settings > Personalization > Colors",
        };
        var systemItem = new ToolStripMenuItem("System (follow Windows)");
        var lightItem = new ToolStripMenuItem("Light");
        var darkItem = new ToolStripMenuItem("Dark");
        var current = GetThemeMode();
        systemItem.Checked = current == ThemeMode.System;
        lightItem.Checked = current == ThemeMode.Light;
        darkItem.Checked = current == ThemeMode.Dark;
        void SelectTheme(ThemeMode mode)
        {
            SetThemeMode(mode);
            systemItem.Checked = mode == ThemeMode.System;
            lightItem.Checked = mode == ThemeMode.Light;
            darkItem.Checked = mode == ThemeMode.Dark;
            ApplyThemeToMenu(menu, IsDarkEffective());
        }
        systemItem.Click += (_, _) => SelectTheme(ThemeMode.System);
        lightItem.Click += (_, _) => SelectTheme(ThemeMode.Light);
        darkItem.Click += (_, _) => SelectTheme(ThemeMode.Dark);
        themeItem.DropDownItems.Add(systemItem);
        themeItem.DropDownItems.Add(lightItem);
        themeItem.DropDownItems.Add(darkItem);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Application.Exit();

        menu.Items.Add(startItem);
        menu.Items.Add(themeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        ApplyThemeToMenu(menu, IsDarkEffective());
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
            case EVENT_OBJECT_NAMECHANGE:
                // Explorer first shows "File Explorer", then the folder name: retry the
                // immediate restore the moment the title settles instead of waiting for
                // the next poll, so a reopened maximized folder snaps without flashing.
                if (idObject == OBJID_WINDOW)
                {
                    ShowQueue.Enqueue(hwnd);
                    _wakeEvent?.Set();
                }
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
    // watcher to re-maximize them against the new work area. Also handles
    // TaskbarCreated (Explorer restart) which would otherwise leave the tray
    // icon invisible while the process keeps running.
    private sealed class DisplayChangeFilter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                _displayChanged = true;
                _wakeEvent?.Set();
            }
            else if (m.Msg == WM_SETTINGCHANGE)
            {
                // Broadcast when Settings > Personalization > Colors changes
                // ("ImmersiveColorSet"): re-evaluate the follow-Windows theme.
                try { OnSystemThemeMaybeChanged(); } catch { }
            }
            else if (_wmTaskbarCreated != 0 && m.Msg == (int)_wmTaskbarCreated)
            {
                Log("TaskbarCreated received (Explorer restarted); recreating tray icon (filter)");
                try { RecreateTrayIcon(); } catch (Exception ex) { Log("tray recreate failed: " + ex.Message); }
                _wakeEvent?.Set();
            }
            return false;
        }
    }

    private sealed class TaskbarForm : Form
    {
        public TaskbarForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Visible = false;
            WindowState = FormWindowState.Minimized;
        }
        protected override void WndProc(ref Message m)
        {
            if (_wmTaskbarCreated != 0 && m.Msg == (int)_wmTaskbarCreated)
            {
                Log("TaskbarCreated received (Explorer restarted); recreating tray icon (wndproc)");
                try { RecreateTrayIcon(); } catch (Exception ex) { Log("tray recreate failed: " + ex.Message); }
                _wakeEvent?.Set();
            }
            else if (m.Msg == WM_DISPLAYCHANGE)
            {
                _displayChanged = true;
                _wakeEvent?.Set();
            }
            else if (m.Msg == WM_SETTINGCHANGE)
            {
                try { OnSystemThemeMaybeChanged(); } catch { }
            }
            base.WndProc(ref m);
        }
        protected override void SetVisibleCore(bool value) { if (!IsHandleCreated) base.SetVisibleCore(false); else base.SetVisibleCore(value); }
    }

    private sealed class TaskbarNativeWindow : NativeWindow
    {
        public TaskbarNativeWindow()
        {
            var cp = new CreateParams
            {
                Caption = "WindowMaximizerMsg",
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = 0x80, // WS_EX_TOOLWINDOW
                Parent = IntPtr.Zero,
            };
            CreateHandle(cp);
        }
        protected override void WndProc(ref Message m)
        {
            if (_wmTaskbarCreated != 0 && m.Msg == (int)_wmTaskbarCreated)
            {
                Log("TaskbarCreated received (Explorer restarted); recreating tray icon (native)");
                try { RecreateTrayIcon(); } catch (Exception ex) { Log("tray recreate failed: " + ex.Message); }
                _wakeEvent?.Set();
            }
            else if (m.Msg == WM_DISPLAYCHANGE)
            {
                _displayChanged = true;
                _wakeEvent?.Set();
            }
            else if (m.Msg == WM_SETTINGCHANGE)
            {
                try { OnSystemThemeMaybeChanged(); } catch { }
            }
            base.WndProc(ref m);
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Called from the watcher thread when a restore gave up; marshals to the tray.
    private static void NotifyRestoreFailed(string key)
    {
        try
        {
            _notifyIcon?.ShowBalloonTip(8000, "WindowMaximizer",
                $"Couldn't restore \"{key}\" to maximized. The window is likely running at a higher " +
                "privilege level (e.g. SYSTEM) that Windows blocks even from admin apps.",
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

    private static void CreateTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = LoadIcon();
        if (_notifyIcon != null) { try { _notifyIcon.Visible = false; _notifyIcon.Dispose(); } catch { } }
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "WindowMaximizer",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
    }

    private static void RecreateTrayIcon()
    {
        // Must run on the UI thread (message pump). If called from
        // SystemEvents thread, marshal via the hidden form.
        if (_hiddenForm != null && _hiddenForm.InvokeRequired)
        {
            try { _hiddenForm.BeginInvoke(new Action(RecreateTrayIcon)); } catch { }
            return;
        }
        try
        {
            // If icon is already visible and Explorer just restarted, Windows
            // clears the icon. Toggle Visible to force re-add.
            if (_notifyIcon != null)
            {
                try { _notifyIcon.Visible = false; } catch { }
                try { _notifyIcon.Visible = true; return; } catch { }
            }
            CreateTrayIcon();
            Log("tray recreated");
        }
        catch (Exception ex) { Log("recreate tray failed: " + ex.Message); }
    }

    private static void RehookWinEvent()
    {
        try
        {
            if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
            _winEventProc = WinEventProc;
            _winEventHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            Log(_winEventHook == IntPtr.Zero ? "rehook WinEvent failed" : "rehook WinEvent ok (resume)");
        }
        catch (Exception ex) { Log("rehook failed: " + ex.Message); }
    }

    // ---- Theme (follow Windows + manual override) ----
    //
    // ThemeMode.System (default) reads HKCU\...\Personalize\AppsUseLightTheme
    // (0 = dark, 1 = light) so the tray menu matches Settings > Personalization
    // > Colors > "Choose your mode". Light/Dark override it. Stored as a plain
    // "ThemeMode" string value under RegRoot (window records use 40-char hex
    // digest names, so there is no collision).

    private enum ThemeMode { System, Light, Dark }

    private const string ThemeValueName = "ThemeMode";
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ThemeMode GetThemeMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegRoot);
            var raw = key?.GetValue(ThemeValueName) as string;
            if (Enum.TryParse<ThemeMode>(raw, ignoreCase: true, out var mode)
                && Enum.IsDefined(typeof(ThemeMode), mode))
                return mode;
        }
        catch { }
        return ThemeMode.System;
    }

    private static void SetThemeMode(ThemeMode mode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegRoot);
            key.SetValue(ThemeValueName, mode.ToString(), RegistryValueKind.String);
            // Drop the cached read handle: RecordState caches RegRoot and only
            // expects DWORD hash values; reopen so a stale handle never hides
            // the new string value (and vice versa).
            try { _regRead?.Dispose(); } catch { }
            _regRead = null;
            Log($"theme -> {mode} (effective {(IsDarkEffective() ? "dark" : "light")})");
        }
        catch (Exception ex) { Log("theme save failed: " + ex.Message); }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 0;
        }
        catch { }
        return false; // missing key = light
    }

    private static bool IsDarkEffective()
    {
        var mode = GetThemeMode();
        return mode == ThemeMode.Dark || (mode == ThemeMode.System && IsSystemDark());
    }

    private static void OnSystemThemeMaybeChanged()
    {
        try
        {
            // Manual Light/Dark never follows Windows; nothing to refresh.
            if (GetThemeMode() != ThemeMode.System) return;
            if (_hiddenForm != null && _hiddenForm.InvokeRequired)
            {
                try { _hiddenForm.BeginInvoke(new Action(OnSystemThemeMaybeChanged)); } catch { }
                return;
            }
            RefreshTheme();
        }
        catch { }
    }

    private static void RefreshTheme()
    {
        try
        {
            var menu = _notifyIcon?.ContextMenuStrip;
            if (menu == null) return;
            ApplyThemeToMenu(menu, IsDarkEffective());
        }
        catch { }
    }

    private static Font? _fluentFont;

    private static Font FluentFont()
    {
        if (_fluentFont != null) return _fluentFont;
        foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI" })
        {
            try { _fluentFont = new Font(name, 9f, FontStyle.Regular, GraphicsUnit.Point); break; }
            catch { }
        }
        _fluentFont ??= SystemFonts.MenuFont ?? new Font("Segoe UI", 9f);
        return _fluentFont;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void ApplyThemeToMenu(ContextMenuStrip menu, bool dark)
    {
        try
        {
            try { (menu.Renderer as IDisposable)?.Dispose(); } catch { }
            Color bg = dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(249, 249, 249);
            Color fg = dark ? Color.White : Color.FromArgb(27, 27, 27);
            menu.Renderer = new FluentMenuRenderer(dark);
            menu.BackColor = bg;
            menu.ForeColor = fg;
            menu.Font = FluentFont();
            menu.Padding = new Padding(4);
            try { menu.ShowImageMargin = false; } catch { }
            try { menu.ShowCheckMargin = true; } catch { }
            foreach (ToolStripItem item in menu.Items)
                ApplyThemeToItem(item, dark);
            // Round corners + Mica + immersive dark frame for the popup.
            ApplyFluentChrome(menu, dark);
            foreach (ToolStripItem item in menu.Items)
                if (item is ToolStripMenuItem mi && mi.HasDropDownItems)
                    StyleDropDown(mi.DropDown, dark);
        }
        catch { }
    }

    private static void StyleDropDown(ToolStripDropDown dropDown, bool dark)
    {
        try
        {
            Color bg = dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(249, 249, 249);
            Color fg = dark ? Color.White : Color.FromArgb(27, 27, 27);
            dropDown.BackColor = bg;
            dropDown.ForeColor = fg;
            dropDown.Font = FluentFont();
            dropDown.Padding = new Padding(4);
            try
            {
                if (dropDown is ToolStripDropDownMenu m)
                {
                    m.ShowImageMargin = false;
                    m.ShowCheckMargin = true;
                }
            }
            catch { }
            foreach (ToolStripItem sub in dropDown.Items)
                ApplyThemeToItem(sub, dark);
        }
        catch { }
        ApplyFluentChrome(dropDown, dark);
    }

    private static void ApplyThemeToItem(ToolStripItem item, bool dark)
    {
        try
        {
            Color bg = dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(249, 249, 249);
            Color fg = dark ? Color.White : Color.FromArgb(27, 27, 27);
            Color disabled = dark ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText;
            if (item is ToolStripSeparator) return; // drawn by the renderer
            item.BackColor = bg;
            item.ForeColor = !item.Enabled ? disabled : fg;
            item.Font = FluentFont();
            try { item.Padding = new Padding(10, 5, 10, 5); } catch { }
            try { item.Margin = new Padding(0); } catch { }
            if (item is ToolStripMenuItem mi)
            {
                foreach (ToolStripItem sub in mi.DropDownItems)
                    ApplyThemeToItem(sub, dark);
                StyleDropDown(mi.DropDown, dark);
            }
        }
        catch { }
    }

    private static void ApplyFluentChrome(ToolStripDropDown dropDown, bool dark)
    {
        void Apply()
        {
            try
            {
                if (!dropDown.IsHandleCreated) return;
                int useDark = dark ? 1 : 0;
                try { DwmSetWindowAttribute(dropDown.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)); } catch { }
                try
                {
                    int corner = DWMWCP_ROUND;
                    DwmSetWindowAttribute(dropDown.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                }
                catch { }
                try
                {
                    int backdrop = DWMSBT_TRANSIENTWINDOW;
                    DwmSetWindowAttribute(dropDown.Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
                catch { } // Win10: attribute unknown, ignore
                try { SetWindowTheme(dropDown.Handle, dark ? "DarkMode_Explorer" : null, null); } catch { }
            }
            catch { }
        }
        try
        {
            if (dropDown.IsHandleCreated)
                Apply();
            else
            {
                void Handler(object? s, EventArgs e)
                {
                    try { dropDown.HandleCreated -= Handler; } catch { }
                    Apply();
                }
                dropDown.HandleCreated -= Handler;
                dropDown.HandleCreated += Handler;
            }
        }
        catch { }
    }

    private static void ApplyImmersiveDarkMode(ToolStripDropDown dropDown, bool dark) =>
        ApplyFluentChrome(dropDown, dark);

    private sealed class FluentColorTable : ProfessionalColorTable
    {
        private readonly bool _dark;
        public FluentColorTable(bool dark) { _dark = dark; }
        private Color Bg => _dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(249, 249, 249);
        private Color Hover => _dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(234, 234, 234);
        private Color Border => _dark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(219, 219, 219);
        private Color Sep => _dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(224, 224, 224);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuBorder => Border;
        public override Color ToolStripBorder => Color.Transparent;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
        public override Color SeparatorDark => Sep;
        public override Color SeparatorLight => Sep;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientMiddle => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonPressedGradientBegin => Hover;
        public override Color ButtonPressedGradientMiddle => Hover;
        public override Color ButtonPressedGradientEnd => Hover;
        public override Color GripDark => Border;
        public override Color GripLight => Border;
        public override Color OverflowButtonGradientBegin => Bg;
        public override Color OverflowButtonGradientMiddle => Bg;
        public override Color OverflowButtonGradientEnd => Bg;
        public override Color ToolStripGradientBegin => Bg;
        public override Color ToolStripGradientMiddle => Bg;
        public override Color ToolStripGradientEnd => Bg;
        public override Color StatusStripGradientBegin => Bg;
        public override Color StatusStripGradientEnd => Bg;
    }

    private sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _dark;
        public FluentMenuRenderer(bool dark) : base(new FluentColorTable(dark)) { _dark = dark; RoundedEdges = true; }
        private Color Bg => _dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(249, 249, 249);
        private Color Hover => _dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(234, 234, 234);
        private Color Text => _dark ? Color.White : Color.FromArgb(27, 27, 27);
        private Color Disabled => _dark ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText;
        private Color Sep => _dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(224, 224, 224);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            try
            {
                using var b = new SolidBrush(Bg);
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
            catch { base.OnRenderToolStripBackground(e); }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Let DWM draw the rounded Win11 border; suppress the square one.
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // No gutter divider in the Win11 language (ShowImageMargin=false anyway).
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            try
            {
                if (!e.Item.Enabled) return;
                bool selected = e.Item.Selected ||
                    (e.Item is ToolStripMenuItem mi && mi.DropDown.Visible);
                if (!selected) return;
                var rect = new Rectangle(4, 1, Math.Max(0, e.Item.Width - 8), Math.Max(0, e.Item.Height - 2));
                if (rect.Width <= 0 || rect.Height <= 0) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = RoundedRect(rect, 4);
                using var brush = new SolidBrush(Hover);
                e.Graphics.FillPath(brush, path);
            }
            catch { base.OnRenderMenuItemBackground(e); }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Disabled : Text;
            e.TextFont = FluentFont();
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Fluent check: glyph only, no box. Drawn centered in the check area.
            try
            {
                var rc = e.ImageRectangle;
                if (rc.Width <= 0 || rc.Height <= 0)
                    rc = new Rectangle(10, e.Item.Height / 2 - 6, 12, 12);
                float cx = rc.X + rc.Width / 2f;
                float cy = rc.Y + rc.Height / 2f;
                using var pen = new Pen(!e.Item.Enabled ? Disabled : Text, 1.8f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                };
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, new[]
                {
                    new PointF(cx - 4.5f, cy + 0.5f),
                    new PointF(cx - 1f, cy + 3.5f),
                    new PointF(cx + 5f, cy - 4f),
                });
            }
            catch { base.OnRenderItemCheck(e); }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // Fluent chevron ">" instead of the WinForms triangle.
            try
            {
                var rc = e.ArrowRectangle;
                float cx = rc.X + rc.Width / 2f;
                float cy = rc.Y + rc.Height / 2f;
                using var pen = new Pen((e.Item?.Enabled ?? true) ? Text : Disabled, 1.6f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                };
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, new[]
                {
                    new PointF(cx - 2.5f, cy - 4f),
                    new PointF(cx + 1.5f, cy),
                    new PointF(cx - 2.5f, cy + 4f),
                });
            }
            catch { base.OnRenderArrow(e); }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            try
            {
                int y = e.Item.Height / 2;
                int left = 12, right = e.Item.Width - 12;
                if (right <= left) return;
                using var pen = new Pen(Sep, 1f);
                e.Graphics.DrawLine(pen, left, y, right, y);
            }
            catch { base.OnRenderSeparator(e); }
        }
    }

    private static void EnsureAutoStartTaskHealthy()
    {
        if (!IsAutoStartEnabled()) return;
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/query");
            psi.ArgumentList.Add("/tn");
            psi.ArgumentList.Add(AutoStartTaskName);
            psi.ArgumentList.Add("/xml");
            using var p = Process.Start(psi);
            if (p == null) return;
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            bool bad = xml.Contains("<DisallowStartIfOnBatteries>true") ||
                       xml.Contains("<StopIfGoingOnBatteries>true") ||
                       xml.Contains("<StopOnIdleEnd>true") ||
                       !xml.Contains("<ExecutionTimeLimit>PT0S");
            if (bad)
            {
                Log("stale task detected (battery/idle kill); repairing");
                SetAutoStart(true);
                Log("task repaired");
            }
        }
        catch { }
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
               "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>" +
               "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
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

    // Maximizes a window WITHOUT stealing keyboard focus. The previous
    // implementation used ShowWindow(SW_SHOWMAXIMIZED), whose activation yanked
    // keyboard focus away from whatever the user was typing in - every call here
    // can fire on a background window (retry loop, re-assert, re-launch spike, a
    // hidden window re-showing). SetWindowPlacement applies the maximize without
    // activating; verified: the foreground window keeps focus through it.
    private static void MaximizeNoSteal(IntPtr hwnd)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp))
        {
            ShowWindow(hwnd, SW_SHOWMAXIMIZED); // placement unavailable: legacy path (activates)
            return;
        }
        wp.showCmd = SW_SHOWMAXIMIZED;
        SetWindowPlacement(hwnd, ref wp);
    }

    private static bool IsValidNormalRect(RECT rc) =>
        rc.right > rc.left && rc.bottom > rc.top;

    private static bool SameRect(RECT a, RECT b) =>
        a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;

    private static bool IsRectOnScreen(RECT rc)
    {
        try
        {
            var r = Rectangle.FromLTRB(rc.left, rc.top, rc.right, rc.bottom);
            foreach (var s in Screen.AllScreens)
            {
                try
                {
                    if (s.WorkingArea.IntersectsWith(r))
                        return true;
                }
                catch { }
            }
            // No screen intersects: after a monitor unplug the saved coords would
            // land off-screen and strand the window; let the app pick a default.
            return false;
        }
        catch
        {
            return true; // screen query failed: don't block the restore
        }
    }

    // Restores a normal window's saved position/size WITHOUT stealing focus,
    // mirroring MaximizeNoSteal. A minimized-from-normal window keeps its
    // minimized state (only its restore bounds are updated) so we never yank
    // a minimized window up; a normal window gets its bounds applied.
    private static void RestoreNormalBoundsNoSteal(IntPtr hwnd, RECT rc)
    {
        if (IsDialogClass(GetText(hwnd, GetClassName)))
        {
            RestoreDialogPositionOnly(hwnd, rc);
            return;
        }
        if (!IsValidNormalRect(rc) || !IsRectOnScreen(rc))
            return;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp))
            return;
        if (wp.showCmd == SW_SHOWMAXIMIZED)
            return; // maximized: the maximize path owns this window
        if (SameRect(wp.rcNormalPosition, rc))
            return; // already there: avoid flicker
        if (wp.showCmd == SW_SHOWMINIMIZED)
        {
            // Minimized (not from maximized - caller checked): update the bounds
            // it will restore to, but stay minimized.
            wp.rcNormalPosition = rc;
            SetWindowPlacement(hwnd, ref wp);
            return;
        }
        if (wp.showCmd != SW_SHOWNORMAL)
            return; // hidden/other: leave alone
        wp.showCmd = SW_SHOWNORMAL;
        wp.flags = 0;
        wp.rcNormalPosition = rc;
        SetWindowPlacement(hwnd, ref wp);
    }

    // Move-only position restore for dialogs: same saved top-left, but the
    // current size is kept, so no WM_SIZE / child re-layout fires mid-init.
    // Posted async without activating or reordering, so it can't steal focus
    // or block the watcher on a busy dialog.
    private static void RestoreDialogPositionOnly(IntPtr hwnd, RECT rc)
    {
        if (!IsValidNormalRect(rc) || !IsRectOnScreen(rc))
            return;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp))
            return;
        if (wp.showCmd == SW_SHOWMAXIMIZED)
            return; // maximized: the maximize path owns this window
        if (wp.rcNormalPosition.left == rc.left && wp.rcNormalPosition.top == rc.top)
            return; // already there: avoid flicker
        if (wp.showCmd == SW_SHOWMINIMIZED)
        {
            // Invisible: updating the restore bounds can't disturb init.
            wp.rcNormalPosition = rc;
            SetWindowPlacement(hwnd, ref wp);
            return;
        }
        if (wp.showCmd != SW_SHOWNORMAL)
            return; // hidden/other: leave alone
        SetWindowPos(hwnd, IntPtr.Zero, rc.left, rc.top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    private static void WatcherLoop(bool verbose)
    {
        var previous = new Dictionary<IntPtr, WinInfo>();
        // ---- main loop ----
        // (re-launch poller and loop body below, after window-state declarations)
        // key -> when to stop forcing it maximized (a window may fight back during startup)
        var restoreUntil = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // Per key: when the retry loop last tried to maximize (throttled to 1/s, see
        // the restoreUntil block). Pruned alongside restoreUntil in the sweep below.
        var lastRestoreTry = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // Closes are grouped per key for a few seconds: an app can close several
        // windows at once (e.g. Win11 Notepad owns several visible "Notepad"
        // windows). The window whose state changed most recently decides the
        // group's state (see lastStateAt below). Bounds carries that winner's
        // last normal position/size so a normal close also restores geometry.
        var pendingCloses = new Dictionary<string, (bool Maximized, DateTime At, DateTime ChangeAt, RECT Bounds, bool HasBounds)>(StringComparer.Ordinal);
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
        // process, so it is never triggered. ponytail: 100ms poll, only for knownExes; stops
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
        var deadPids = new List<uint>();
        // exe image name (no extension) -> live process count, rebuilt once per
        // census tick so each knownExe is a dictionary lookup, not a full scan.
        var liveCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long lastCensusMs = 0, maxCensusMs = 0; // heartbeat diagnostics
        _ = Task.Run(async () =>
        {
            while (!spikeCts.IsCancellationRequested)
            {
                // Idle: no maximized window, so re-launch re-max can't matter. Skip the
                // process census entirely and sleep, letting CPU drop to zero.
                if (!_spikeActive)
                {
                    try { await Task.Delay(100, spikeCts.Token); } catch { break; }
                    continue;
                }
                try
                {
                    var censusSw = Stopwatch.StartNew();
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
                    deadPids.Clear();
                    foreach (var kv in pidNames)
                        if (!livePids.Contains(kv.Key)) deadPids.Add(kv.Key);
                    foreach (var dead in deadPids)
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

                    // One pass over pidNames builds per-image live counts; each knownExe
                    // is then a single lookup (was a full scan of all names per exe).
                    liveCounts.Clear();
                    foreach (var name in pidNames.Values)
                        liveCounts[name] = liveCounts.TryGetValue(name, out int n) ? n + 1 : 1;
                    foreach (var exe in knownExes.ToList())
                    {
                        string want = Path.GetFileNameWithoutExtension(exe);
                        int live = liveCounts.TryGetValue(want, out int n) ? n : 0;
                        lock (spikeLock)
                        {
                            int baseN = exeBaseline.TryGetValue(exe, out var b) ? b : live;
                            if (live > baseN) exeSpikeAt[exe] = DateTime.Now;   // a 2nd instance appeared
                            exeBaseline[exe] = Math.Max(baseN, live);
                        }
                    }
                    lastCensusMs = censusSw.ElapsedMilliseconds;
                    if (lastCensusMs > maxCensusMs) maxCensusMs = lastCensusMs;
                }
                catch { }
                // ponytail: 100ms sampling (one scan per tick); a re-launch stub shorter
                // than ~100ms may slip through. If re-launch demotes start being missed,
                // drop to 50ms - still one scan per tick.
                try { await Task.Delay(100, spikeCts.Token); } catch { break; }
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
        // A sibling counts as a "reopen" only while young: a long-existing
        // same-folder window must not rescue a deliberate Restore (two same-folder
        // windows may legitimately differ - one maximized, one normal).
        var siblingReopenGrace = TimeSpan.FromSeconds(3.0);
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
        // Last known normal position/size per hwnd, updated whenever a window is
        // seen in a normal (or minimized-from-normal) state. The snapshot only
        // runs every ~2s, so a move+close inside one interval would otherwise
        // record a stale rect; the fast pass keeps this fresh at 200ms while
        // any maximized intent is live.
        var lastNormalBounds = new Dictionary<IntPtr, RECT>();
        // Windows whose normal bounds were already restored after opening (one-shot):
        // without this the immediate path (re-checked every 50ms for 5s while the
        // title settles) would fight a user move made right after opening.
        var boundsRestored = new HashSet<IntPtr>();
        int tick = 0;
        var nextFull = DateTime.MinValue;
        // Foreground tracking to separate explicit Restore (demote while already
        // foreground) from programmatic demote (demote while background, or becoming
        // foreground coincident with the demote, e.g. Start-menu re-activating an
        // already-open maximized folder). Sampled every loop iteration (~200ms when
        // anything maximized is live), so prevFg is the foreground ~one tick ago.
        IntPtr prevFgHwnd = IntPtr.Zero;
        IntPtr lastFgHwnd = IntPtr.Zero;
        // Full-pass diagnostics for the verbose heartbeat.
        var passStart = DateTime.Now;
        long lastPassMs = 0, maxPassMs = 0;

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
                // title settle / visibility race resolves promptly. New windows pending
                // title settle poll fastest (50ms, brief) so a folder reopen snaps
                // ms after Explorer sets its title instead of flashing normal.
                bool signaled = _wakeEvent?.WaitOne(restoreCandidates.Count > 0 ? 50
                    : (fast || closedAt.Count > 0 || reAppearMax.Count > 0) ? FastPollMs : FallbackPollMs) ?? false;
                bool fullDue = DateTime.Now >= nextFull;
                // Sample foreground once per iteration for the demote-cause check below.
                IntPtr fgNow = IntPtr.Zero;
                try { fgNow = GetForegroundWindow(); } catch { }
                prevFgHwnd = lastFgHwnd;
                lastFgHwnd = fgNow;

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
                    if (!candidateSince.TryGetValue(hwnd, out var candSince)
                        || DateTime.Now - candSince > titleSettleWindow)
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
                                MaximizeNoSteal(hwnd);
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
                        // Keep the normal position/size fresh: a move+close inside one
                        // full-pass interval would otherwise record a stale rect.
                        if (!maximizedNow && IsValidNormalRect(placement.rcNormalPosition))
                            lastNormalBounds[hwnd] = placement.rcNormalPosition;
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
                        {
                            unMaximizedSince[hwnd] = DateTime.Now;
                            // Start-menu / taskbar re-activating the same window demotes
                            // it while becoming foreground (never while staying
                            // foreground - explicit Restore stays foreground). Rescue
                            // became-foreground demotes at once; plain background
                            // demotes wait below for a sibling (new-window reopen)
                            // instead of fighting a background Restore. Never right
                            // after WSK maximized (fight-back).
                            if (intendedMaximized.Contains(hwnd) && placement.showCmd == SW_SHOWNORMAL)
                            {
                                bool became = hwnd == fgNow && hwnd != prevFgHwnd;
                                bool wskRecent = lastMaxAssert.TryGetValue(hwnd, out var lma)
                                    && DateTime.Now - lma <= reAssertGrace;
                                if (became && !wskRecent)
                                {
                                    Log($"restored {hwnd:X} (maximized->normal on re-activate, intent kept)");
                                    lastMaxAssert[hwnd] = DateTime.Now;
                                    MaximizeNoSteal(hwnd);
                                    unMaximizedSince.Remove(hwnd);
                                }
                            }
                        }
                        else if (DateTime.Now - since > teardownGrace)
                        {
                            lastStateAt[hwnd] = (false, since); // commit the un-maximize at its start
                            intendedMaximized.Remove(hwnd);
                            unMaximizedSince.Remove(hwnd);
                        }
                        // Re-assert maximize ONLY when a re-launch spike (a 2nd short-lived
                        // process) explains the revert - that's the single-instance app
                        // demoting its own window. A genuine user Restore has no spike, so it
                        // is never re-maximized.
                        else if (lastMaximizedAt.TryGetValue(hwnd, out var lastMax) && DateTime.Now - lastMax <= reAssertGrace
                            && previous.TryGetValue(hwnd, out var pinf) && exeSpikeAt.TryGetValue(pinf.Exe, out var spiked) && DateTime.Now - spiked <= relaunchGrace)
                        {
                            lastMaxAssert[hwnd] = DateTime.Now;
                            Log($"re-assert {hwnd:X} (re-launch spike reverted a maximized window)");
                            MaximizeNoSteal(hwnd);
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
                                MaximizeNoSteal(hwnd);
                            }
                        }
                        lastShowCmd[hwnd] = placement.showCmd;
                    }
                    continue;
                }

                passStart = DateTime.Now;
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
                            MaximizeNoSteal(hwnd);
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
                // A pid change on the same HWND value means the OS reused the handle
                // for a new process's window: the old window closed and a new one
                // opened. Without this the old close is never recorded and the new
                // window looks like a continuing one, so its restore is missed.
                foreach (var (hwnd, info) in previous)
                {
                    if (!current.ContainsKey(hwnd)
                        || (current.TryGetValue(hwnd, out var curNow) && curNow.Pid != info.Pid))
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
                        boundsRestored.Remove(hwnd);
                        closedAt[hwnd] = DateTime.Now;
                        // Stage the normal position/size for a normal close: prefer the
                        // fresh fast-pass rect (move+close inside one full interval),
                        // else the last snapshot's rect. Maximized closes don't stage
                        // bounds - the maximize path owns the next open.
                        RECT stagedBounds = default;
                        bool stagedHasBounds = false;
                        if (!wasMaximized)
                        {
                            if (lastNormalBounds.TryGetValue(hwnd, out var fresh) && IsValidNormalRect(fresh))
                            { stagedBounds = fresh; stagedHasBounds = true; }
                            else if (IsValidNormalRect(info.NormalBounds))
                            { stagedBounds = info.NormalBounds; stagedHasBounds = true; }
                        }
                        lastNormalBounds.Remove(hwnd);
                        if (pendingCloses.TryGetValue(info.Key, out var pc))
                        {
                            if (changeAt > pc.ChangeAt)
                                pendingCloses[info.Key] = (wasMaximized, DateTime.Now, changeAt, stagedBounds, stagedHasBounds);
                            else
                                pendingCloses[info.Key] = (pc.Maximized, DateTime.Now, pc.ChangeAt, pc.Bounds, pc.HasBounds);
                        }
                        else
                            pendingCloses[info.Key] = (wasMaximized, DateTime.Now, changeAt, stagedBounds, stagedHasBounds);
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
                        if (!pc.Maximized && pc.HasBounds)
                            RecordNormalBounds(key, pc.Bounds);
                    }
                }

                // Track when each window was last seen maximized (see maximizeHistory above).
                // Also remember which exes are live, so the per-iteration re-launch detector
                // (above) only refreshes process counts for exes we already track.
                foreach (var (hwnd, info) in current)
                {
                    knownExes.Add(info.Exe);
                    maximizedIntent[hwnd] = info.Maximized;
                    if (!info.Maximized && IsValidNormalRect(info.NormalBounds))
                        lastNormalBounds[hwnd] = info.NormalBounds;
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
                        {
                            unMaximizedSince[hwnd] = DateTime.Now;
                            // Same-folder reopen demote (Start-menu / taskbar): same HWND,
                            // same folder (BaseKey unchanged, not navigated away), normal
                            // demote while background/became (never stayed-foreground
                            // explicit Restore). Rescue at once unless WSK just maximized
                            // (user fighting back). Plain background with no sibling and
                            // no became waits below for the sibling/activation instead
                            // of fighting a background Restore.
                            if (intendedMaximized.Contains(hwnd) && info.ShowCmd == SW_SHOWNORMAL
                                && previous.TryGetValue(hwnd, out var prevInfo) && prevInfo.BaseKey == info.BaseKey)
                            {
                                bool stayed = hwnd == fgNow && hwnd == prevFgHwnd;
                                bool became = hwnd == fgNow && hwnd != prevFgHwnd;
                                bool wskRecent = lastMaxAssert.TryGetValue(hwnd, out var lma)
                                    && DateTime.Now - lma <= reAssertGrace;
                                bool siblingRecent = false;
                                if (!stayed && !wskRecent)
                                {
                                    foreach (var (oh, oinfo) in current)
                                    {
                                        if (oh == hwnd || oinfo.BaseKey != info.BaseKey) continue;
                                        if (!(oinfo.Maximized || intendedMaximized.Contains(oh))) continue;
                                        bool brandNew = !previous.ContainsKey(oh);
                                        bool justSeen = firstSeenAt.TryGetValue(oh, out var sibSeen)
                                            && DateTime.Now - sibSeen <= siblingReopenGrace;
                                        if (brandNew || justSeen) { siblingRecent = true; break; }
                                    }
                                }
                                if ((became || siblingRecent) && !wskRecent && !stayed)
                                {
                                    Log($"restored {hwnd:X} (maximized->normal on same-folder reopen, intent kept)");
                                    lastMaxAssert[hwnd] = DateTime.Now;
                                    MaximizeNoSteal(hwnd);
                                    unMaximizedSince.Remove(hwnd);
                                }
                            }
                        }
                        else if (DateTime.Now - since > teardownGrace)
                        {
                            lastStateAt[hwnd] = (false, since);
                            intendedMaximized.Remove(hwnd);
                        }
                        else if (intendedMaximized.Contains(hwnd) && info.ShowCmd == SW_SHOWNORMAL
                            && previous.TryGetValue(hwnd, out var prevInfo2) && prevInfo2.BaseKey == info.BaseKey)
                        {
                            // New-after-demote / delayed-activate (within the grace, before
                            // the sustained clear above): same-folder sibling just appeared,
                            // or became-foreground this tick. Never for stayed-foreground
                            // genuine Restores (which never became, and whose sibling
                            // coincidence is ignored).
                            bool stayed2 = hwnd == fgNow && hwnd == prevFgHwnd;
                            bool became2 = hwnd == fgNow && hwnd != prevFgHwnd;
                            bool wskRecent2 = lastMaxAssert.TryGetValue(hwnd, out var lma2)
                                && DateTime.Now - lma2 <= reAssertGrace;
                            bool siblingRecent2 = false;
                            if (!stayed2 && !wskRecent2)
                            {
                                foreach (var (oh, oinfo) in current)
                                {
                                    if (oh == hwnd || oinfo.BaseKey != info.BaseKey) continue;
                                    if (!(oinfo.Maximized || intendedMaximized.Contains(oh))) continue;
                                    bool brandNew = !previous.ContainsKey(oh);
                                    bool justSeen = firstSeenAt.TryGetValue(oh, out var sibSeen)
                                        && DateTime.Now - sibSeen <= siblingReopenGrace;
                                    if (brandNew || justSeen) { siblingRecent2 = true; break; }
                                }
                            }
                            if ((became2 || siblingRecent2) && !wskRecent2 && !stayed2)
                            {
                                Log($"restored {hwnd:X} (maximized->normal on same-folder reopen, intent kept)");
                                lastMaxAssert[hwnd] = DateTime.Now;
                                MaximizeNoSteal(hwnd);
                                unMaximizedSince.Remove(hwnd);
                            }
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
                            MaximizeNoSteal(hwnd);
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
                            else if (pc.HasBounds)
                                RecordNormalBounds(info.Key, pc.Bounds);
                        }
                        else
                            continue; // still settling: don't restore on a stale record yet
                    }
                    if (info.Maximized)
                    {
                        restoreUntil.Remove(info.Key); // already maximized, nothing to do
                        // A reused HWND (new pid) showing maximized is a new window whose
                        // app restored its own state, not a continuing one: watch for a
                        // self-demote like any other reappearance.
                        if (!previous.ContainsKey(hwnd)
                            || (previous.TryGetValue(hwnd, out var prevMax) && prevMax.Pid != info.Pid))
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
                        // Retry at most once per second: this loop runs every fast pass
                        // (~100ms), and each try activates the window - a stubborn window
                        // used to be hammered with activation for up to RestoreGiveUp.
                        if (!lastRestoreTry.TryGetValue(info.Key, out var lastTry)
                            || DateTime.Now - lastTry >= TimeSpan.FromSeconds(1))
                        {
                            lastRestoreTry[info.Key] = DateTime.Now;
                            lastMaxAssert[hwnd] = DateTime.Now;
                            MaximizeNoSteal(hwnd);
                        }
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
                            || (previous.TryGetValue(hwnd, out var prevNew) && prevNew.Pid != info.Pid)
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
                        MaximizeNoSteal(hwnd);
                    }
                    // Normal position/size: a window closed normal reopens with its
                    // saved bounds. Maximized (record or live sibling) takes
                    // precedence above, so reaching here means "should stay normal".
                    // Same isNew/reopen gating as the maximize path so a later
                    // resize by the user is never fought.
                    else if (!boundsRestored.Contains(hwnd)
                        && (!previous.ContainsKey(hwnd)
                            || (previous.TryGetValue(hwnd, out var prevNorm) && prevNorm.Pid != info.Pid)
                            || (previous[hwnd].Key != info.Key && firstSeenAt.TryGetValue(hwnd, out var firstSeenN)
                                && DateTime.Now - firstSeenN <= titleSettleWindow)
                            || (closedAt.TryGetValue(hwnd, out var closedWhenN) && DateTime.Now - closedWhenN <= reopenGrace
                                && !(lastMaxAssert.TryGetValue(hwnd, out var lastRestoredN) && DateTime.Now - lastRestoredN <= reAssertGrace))
                            || (reAppearMax.TryGetValue(hwnd, out var reappearedN) && DateTime.Now - reappearedN <= reAppearGrace
                                && !(lastMaxAssert.TryGetValue(hwnd, out var lastRestoredN2) && DateTime.Now - lastRestoredN2 <= reAssertGrace)))
                        && RecordState(info.Key) == 0
                        && !current.Any(kv => kv.Key != hwnd && kv.Value.BaseKey == info.BaseKey
                            && (kv.Value.Maximized || intendedMaximized.Contains(kv.Key)))
                        && TryGetNormalBounds(info.Key, out var savedBounds)
                        && !SameRect(info.NormalBounds, savedBounds))
                    {
                        Log($"restored {hwnd:X} {info.Key} bounds {savedBounds.left},{savedBounds.top},{savedBounds.right},{savedBounds.bottom} (normal{(IsDialogClass(info.Class) ? ", dialog-pos" : "")})");
                        RestoreNormalBoundsNoSteal(hwnd, savedBounds);
                        boundsRestored.Add(hwnd);
                    }
                }

                previous = current;

                // Free instance slots for windows that vanished without a full pass
                // (created and destroyed between polls): a burned slot would drift the
                // index upward forever. Windows in `current` keep their assignment.
                foreach (var h in hwndInstance.Keys.ToList())
                    if (!current.ContainsKey(h))
                        hwndInstance.Remove(h);
                foreach (var h in boundsRestored.ToList())
                    if (!current.ContainsKey(h))
                        boundsRestored.Remove(h);
                foreach (var h in closedAt.Keys.ToList())
                    if (DateTime.Now - closedAt[h] > reopenGrace)
                        closedAt.Remove(h);
                foreach (var h in reAppearMax.Keys.ToList())
                    if (DateTime.Now - reAppearMax[h] > reAppearGrace)
                        reAppearMax.Remove(h);
                foreach (var k in lastRestoreTry.Keys.ToList())
                    if (!restoreUntil.ContainsKey(k))
                        lastRestoreTry.Remove(k);
                // lastMaxAssert: every read is gated by reAssertGrace, so entries older
                // than that can never fire - drop them instead of growing forever.
                foreach (var h in lastMaxAssert.Keys.ToList())
                    if (DateTime.Now - lastMaxAssert[h] > reAssertGrace)
                        lastMaxAssert.Remove(h);
                // candidateSince: entries for windows neither live nor being watched
                // (created and destroyed between full passes) would otherwise leak.
                foreach (var h in candidateSince.Keys.ToList())
                    if (!restoreCandidates.Contains(h) && !current.ContainsKey(h))
                        candidateSince.Remove(h);

                // Schedule the next full pass here (not at the top): the counts for
                // closedAt/reAppearMax updated during THIS pass must decide the
                // cadence of the NEXT pass, so a window that just reappeared (max or
                // recently closed) is re-polled in a quarter second, not up to 2s.
                // A demote pending intent (unMaximized + intended overlap, brief 0.65s
                // window) also needs the fast cadence so a same-folder sibling or
                // delayed activation arriving ~500ms after the demote is seen before
                // the sustained clear. New windows pending restore (candidates) need
                // it too, otherwise a rapid 2nd/3rd reopen waits the 2s fallback for
                // its sibling to appear in the snapshot (the "wait 3s" effect).
                bool demotePending = false;
                foreach (var h in unMaximizedSince.Keys)
                    if (intendedMaximized.Contains(h)) { demotePending = true; break; }
                nextFull = DateTime.Now.AddMilliseconds(
                    (closedAt.Count > 0 || reAppearMax.Count > 0 || demotePending || restoreCandidates.Count > 0) ? ReopenPollMs : FallbackPollMs);

                // Routine heartbeat: only in --verbose mode, so the log stays
                // event-only (and small) during normal use.
                lastPassMs = (long)(DateTime.Now - passStart).TotalMilliseconds;
                if (lastPassMs > maxPassMs) maxPassMs = lastPassMs;
                if (++tick % 30 == 0 && verbose)
                    Log($"tick windows={current.Count} restorePending={restoreUntil.Count} " +
                        $"passMs={lastPassMs}/max{maxPassMs} censusMs={lastCensusMs}/max{maxCensusMs} " +
                        $"heapMb={GC.GetTotalMemory(false) / (1024 * 1024)}");
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
        bool TryRestoreNow(IntPtr hwnd, Dictionary<string, (bool Maximized, DateTime At, DateTime ChangeAt, RECT Bounds, bool HasBounds)> pending)
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
                // Explorer shows generic "File Explorer" first, then the folder name
                // (~300-500ms, Explorer internal). For a generic/empty title use the
                // shell window location (available ms after creation, before the title)
                // to get the real folder at once instead of flashing normal: parse the
                // file URL to the expected "Name - File Explorer" key and decide from
                // that (record/sibling, same as below). Unknown locations (shell GUIDs,
                // drive roots, COM hiccups) fall back to waiting for the title.
                if ((title.Length == 0 || title == "File Explorer")
                    && exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && cls == "CabinetWClass")
                {
                    var expected = TryGetExplorerExpectedKey(hwnd, exe, cls);
                    if (expected.HasValue)
                    {
                        var (expTitle, expBase, expKey) = expected.Value;
                        bool expRestore;
                        if (pending.TryGetValue(expKey, out var epc))
                            expRestore = epc.Maximized;
                        else
                        {
                            var erec = RecordState(expKey);
                            if (erec == 1)
                                expRestore = true;
                            else if (erec == 0)
                                expRestore = false;
                            else
                            {
                                expRestore = false;
                                foreach (var (oh, oinfo) in previous)
                                {
                                    if (oh == hwnd || oinfo.BaseKey != expBase) continue;
                                    if (oinfo.Maximized || intendedMaximized.Contains(oh))
                                    { expRestore = true; break; }
                                }
                            }
                        }
                        if (expRestore)
                        {
                            Log($"restored {hwnd:X} {expKey} (immediate, via location for '{expTitle}')");
                            lastMaxAssert[hwnd] = DateTime.Now;
                            MaximizeNoSteal(hwnd);
                            return false;
                        }
                        // Expected says normal (or unknown with no maximized sibling):
                        // fall through to title watching below so a normal folder never
                        // flashes maximized. (Return true = keep watching for settle.)
                        return true;
                    }
                }
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
                    NormalBounds = placement.rcNormalPosition,
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
                    if (rec < 0)
                    {
                        // No record: a same-folder sibling that is currently maximized
                        // (or wants to be) still means "open maximized" (mirrors the
                        // full pass sibling rule). Otherwise the title may still settle
                        // into a recorded key, so keep watching.
                        var baseKey = BuildBaseKey(exe, cls, title);
                        foreach (var (oh, oinfo) in previous)
                        {
                            if (oh == hwnd || oinfo.BaseKey != baseKey) continue;
                            if (oinfo.Maximized || intendedMaximized.Contains(oh))
                            {
                                Log($"restored {hwnd:X} {key} (immediate, sibling maximized)");
                                lastMaxAssert[hwnd] = DateTime.Now;
                                MaximizeNoSteal(hwnd);
                                return false;
                            }
                        }
                        return true;
                    }
                    shouldRestore = rec == 1;
                }
                if (!shouldRestore)
                {
                    // Record says normal: still honor a live maximized sibling of the
                    // same folder (maximize -> reopen must stay maximized even though
                    // the folder was never closed maximized to write a record).
                    var baseKey = BuildBaseKey(exe, cls, title);
                    foreach (var (oh, oinfo) in previous)
                    {
                        if (oh == hwnd || oinfo.BaseKey != baseKey) continue;
                        if (oinfo.Maximized || intendedMaximized.Contains(oh))
                        {
                            shouldRestore = true;
                            break;
                        }
                    }
                    if (!shouldRestore)
                    {
                        // Normal position/size: pending staged bounds win over the
                        // flushed record (quick restart before the close group
                        // flushes). Maximized already returned above, so reaching
                        // here means "should stay normal". One-shot per hwnd: after
                        // the first restore a user move must win, not be fought
                        // for the 5s title-settle window. Dialogs are move-only
                        // (see IsDialogClass), so position alone decides "already
                        // there" for them.
                        if (boundsRestored.Contains(hwnd))
                            return false;
                        RECT nb = default;
                        bool hasNb = false;
                        if (pending.TryGetValue(key, out var pcb) && !pcb.Maximized && pcb.HasBounds)
                        { nb = pcb.Bounds; hasNb = true; }
                        else if (TryGetNormalBounds(key, out var rb))
                        { nb = rb; hasNb = true; }
                        if (hasNb)
                        {
                            bool alreadyThere = IsDialogClass(cls)
                                ? (placement.rcNormalPosition.left == nb.left && placement.rcNormalPosition.top == nb.top)
                                : SameRect(placement.rcNormalPosition, nb);
                            if (alreadyThere)
                            {
                                boundsRestored.Add(hwnd);
                                return false; // already there: no flicker, no log spam
                            }
                            Log($"restored {hwnd:X} {key} bounds {nb.left},{nb.top},{nb.right},{nb.bottom} (immediate, normal{(IsDialogClass(cls) ? ", dialog-pos" : "")})");
                            RestoreNormalBoundsNoSteal(hwnd, nb);
                            boundsRestored.Add(hwnd);
                        }
                        return false;
                    }
                }
                Log($"restored {hwnd:X} {key} (immediate)");
                lastMaxAssert[hwnd] = DateTime.Now;
                MaximizeNoSteal(hwnd);
                return false;
            }
            catch
            {
                return false; // the fast path must never take the watcher down
            }
        }

        // Expected folder key for a still-generic Explorer window, via the shell
        // window location (ms after creation, before Explorer sets the title).
        // Returns null when unavailable (not yet registered/navigated, shell GUID,
        // drive root, COM hiccup): caller falls back to waiting for the title.
        // Local function: watcher thread only, shares hwndInstance for the
        // provisional instance slot (same slot the title path will reuse when the
        // title settles, since the base key matches).
        (string Title, string BaseKey, string Key)? TryGetExplorerExpectedKey(IntPtr hwnd, string exe, string cls)
        {
            try
            {
                _shellWindowsApp ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                if (_shellWindowsApp == null) return null;
                int want;
                try { want = hwnd.ToInt32(); }
                catch { return null; } // 64-bit HWND overflow: fall back to title
                foreach (var win in _shellWindowsApp.Windows())
                {
                    int h;
                    try { h = (int)win.HWND; }
                    catch { continue; }
                    if (h != want) continue;
                    string? loc = null;
                    try { loc = (string)win.LocationURL; } catch { return null; }
                    if (string.IsNullOrEmpty(loc) || !loc.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                        return null; // shell GUID / not navigated yet: wait for title
                    string local;
                    try { local = new Uri(loc).LocalPath; }
                    catch { return null; }
                    string name = Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrEmpty(name)) return null; // drive root: title differs, wait
                    string expTitle = name + " - File Explorer";
                    string expBase = BuildBaseKey(exe, cls, expTitle);
                    int inst = AssignInstance(hwnd, expBase, hwndInstance);
                    string expKey = inst <= 1 ? expBase : $"{expBase}|#{inst}";
                    return (expTitle, expBase, expKey);
                }
                return null; // not registered yet: retry on next poll
            }
            catch
            {
                return null; // COM hiccup: fall back to title watching (safe)
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
        uint selfPid = (uint)Environment.ProcessId;

        // Phase 1: enumerate live windows WITHOUT assigning instances. Assignment
        // needs the full live set first: a window that closed since the last pass
        // must free its slot before a reopened window claims one, or a fast
        // close+reopen within one poll interval lands on |#2 and misses its |#1
        // record (random "forgot" misses).
        var entries = new List<(IntPtr Hwnd, uint Pid, string Exe, string Class, string Title, string BaseKey, bool Maximized, int ShowCmd, RECT NormalBounds)>();

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
            RECT normalBounds = default;
            if (GetWindowPlacement(hwnd, ref placement))
            {
                showCmd = placement.showCmd;
                maximized = placement.showCmd == SW_SHOWMAXIMIZED ||
                            (placement.showCmd == SW_SHOWMINIMIZED && (placement.flags & WPF_RESTORETOMAXIMIZED) != 0);
                normalBounds = placement.rcNormalPosition;
            }

            // Reuse the exe name for windows we already track: the poll shouldn't
            // open a Process handle per window per pass.
            var exe = previous.TryGetValue(hwnd, out var prev) && prev.Pid == pid ? prev.Exe : GetProcessName(pid);
            var title = GetWindowTextSafe(hwnd);
            // A transient empty title read (SendMessageTimeout timing out on a busy
            // thread) must not collapse the window onto the shared exe|class key: that
            // would merge it with unrelated windows (and their records). Keep the last
            // non-empty title we saw for this window instead. Pid-gated so an HWND
            // value reused by a new process never inherits the old title.
            if (title.Length == 0 && prev != null && prev.Pid == pid && prev.Title.Length > 0)
                title = prev.Title;
            entries.Add((hwnd, pid, exe, cls, title, BuildBaseKey(exe, cls, title), maximized, showCmd, normalBounds));
            return true;
        }, IntPtr.Zero);

        // Phase 2: free slots of windows that are gone before assigning, so a
        // reopened window reclaims its freed slot instead of drifting to |#2.
        var live = new HashSet<IntPtr>(entries.Select(e => e.Hwnd));
        foreach (var h in hwndInstance.Keys.ToList())
            if (!live.Contains(h))
                hwndInstance.Remove(h);

        // Phase 3: assign. Windows continuing from the last pass keep their slot
        // (stable across z-order churn); new or retitled windows take the smallest
        // free slot among live siblings. The immediate path's provisional assignment
        // (made while a dead sibling still occupied its slot) is overridden here for
        // windows not in `previous`, so the full pass is authoritative.
        var takenByBase = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (previous.ContainsKey(e.Hwnd)
                && hwndInstance.TryGetValue(e.Hwnd, out var cur)
                && cur.BaseKey == e.BaseKey)
            {
                if (!takenByBase.TryGetValue(e.BaseKey, out var s))
                    takenByBase[e.BaseKey] = s = new HashSet<int>();
                s.Add(cur.Instance);
            }
        }
        var result = new Dictionary<IntPtr, WinInfo>();
        foreach (var e in entries)
        {
            int instance;
            if (previous.ContainsKey(e.Hwnd)
                && hwndInstance.TryGetValue(e.Hwnd, out var cur)
                && cur.BaseKey == e.BaseKey)
            {
                instance = cur.Instance;
            }
            else
            {
                if (!takenByBase.TryGetValue(e.BaseKey, out var taken))
                    takenByBase[e.BaseKey] = taken = new HashSet<int>();
                instance = 1;
                while (taken.Contains(instance)) instance++;
                taken.Add(instance);
            }
            hwndInstance[e.Hwnd] = (e.BaseKey, instance);
            result[e.Hwnd] = new WinInfo
            {
                Pid = e.Pid,
                Exe = e.Exe,
                Class = e.Class,
                Title = e.Title,
                Maximized = e.Maximized,
                ShowCmd = e.ShowCmd,
                NormalBounds = e.NormalBounds,
                Instance = instance,
            };
        }

        return result;
    }

    private static string GetProcessName(uint pid)
    {
        IntPtr hProcess = OpenProcess(PROC_QUERY_LIMITED_INFO, false, pid);
        if (hProcess == IntPtr.Zero) return "unknown.exe";
        try
        {
            var sb = new StringBuilder(260);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                return Path.GetFileName(sb.ToString());
            return "unknown.exe";
        }
        finally
        {
            CloseHandle(hProcess);
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

    // Cached read handle: RecordState runs on every restore check (hot while a
    // window title settles), and OpenSubKey per call was the hot path. Opened
    // lazily on first read; the app is RegRoot's only writer and never deletes
    // it, so the handle stays valid for the process lifetime. Any failure falls
    // back to one fresh open. Read only from the watcher thread.
    private static RegistryKey? _regRead;

    // 1 = record says maximized, 0 = record says normal, -1 = no record.
    private static int RecordState(string key)
    {
        try
        {
            if (_regRead == null)
            {
                _regRead = Registry.CurrentUser.OpenSubKey(RegRoot);
                if (_regRead == null) return -1; // no key yet: no records exist
            }
            return _regRead.GetValue(RegValueName(key)) is int v ? v : -1;
        }
        catch
        {
            // key deleted underneath us or hive hiccup: drop the cache, try once fresh.
            try { _regRead?.Dispose(); } catch { }
            _regRead = null;
            try
            {
                using var fresh = Registry.CurrentUser.OpenSubKey(RegRoot);
                return fresh?.GetValue(RegValueName(key)) is int v ? v : -1;
            }
            catch { return -1; }
        }
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
        SyncShortcutRunState(key, maximized);
    }

    private static string BoundsValueName(string key) => RegValueName(key + "|bounds");

    private static void RecordNormalBounds(string key, RECT rc)
    {
        if (!IsValidNormalRect(rc))
            return;
        try
        {
            using var keyHandle = Registry.CurrentUser.CreateSubKey(RegRoot);
            keyHandle.SetValue(BoundsValueName(key), $"{rc.left},{rc.top},{rc.right},{rc.bottom}", RegistryValueKind.String);
            Log($"closed {key} bounds {rc.left},{rc.top},{rc.right},{rc.bottom} (normal)");
        }
        catch (Exception ex)
        {
            Log("bounds save failed: " + ex.Message);
        }
    }

    private static bool TryGetNormalBounds(string key, out RECT rc)
    {
        rc = default;
        try
        {
            string? raw = null;
            try
            {
                if (_regRead == null)
                {
                    _regRead = Registry.CurrentUser.OpenSubKey(RegRoot);
                    if (_regRead == null) return false;
                }
                raw = _regRead.GetValue(BoundsValueName(key)) as string;
            }
            catch
            {
                try { _regRead?.Dispose(); } catch { }
                _regRead = null;
                try
                {
                    using var fresh = Registry.CurrentUser.OpenSubKey(RegRoot);
                    raw = fresh?.GetValue(BoundsValueName(key)) as string;
                }
                catch { return false; }
            }
            if (string.IsNullOrEmpty(raw))
                return false;
            var parts = raw.Split(',');
            if (parts.Length != 4)
                return false;
            if (!int.TryParse(parts[0], out int l) || !int.TryParse(parts[1], out int t)
                || !int.TryParse(parts[2], out int r) || !int.TryParse(parts[3], out int b))
                return false;
            var cand = new RECT { left = l, top = t, right = r, bottom = b };
            if (!IsValidNormalRect(cand))
                return false;
            rc = cand;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- Shortcut Run-state sync ----
    //
    // The native mechanism: a shortcut (.lnk) whose Properties > Run is set to
    // "Maximized" makes Windows open the app maximized from its very first
    // frame - no flash, no fight. When an app is recorded closing maximized we
    // point its launcher shortcut there (and back to Normal on a normal close),
    // so the two mechanisms agree. Apps with no findable/editable shortcut
    // (UWP frames, Explorer, exes launched directly, launcher-borne games)
    // simply keep using the registry restore above.

    private static object? _shellObj;
    private static dynamic ShellCom => _shellObj ??=
        Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)
        ?? throw new InvalidOperationException("WScript.Shell unavailable");

    // Shell.Application for instant folder lookup (watcher thread only, like the
    // immediate path that uses it: new Explorer windows show generic
    // "File Explorer" ~300-500ms before the folder title, but LocationURL is
    // available ms after creation).
    private static dynamic? _shellWindowsApp;

    private static readonly object LnkLock = new();
    // exe name -> shortcut path, or null once scanned and not found (so a
    // shortcut-less app costs one directory scan ever, not one per close).
    private static readonly Dictionary<string, string?> LnkForExe = new(StringComparer.OrdinalIgnoreCase);

    private static void SyncShortcutRunState(string key, bool maximized)
    {
        try
        {
            var exe = key.Split('|')[0];
            // Hosts that carry other apps' windows: their "shortcut" isn't the app's.
            if (exe.Equals(UwpFrameExe, StringComparison.OrdinalIgnoreCase)
                || exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                return;

            string? lnk;
            lock (LnkLock)
            {
                if (!LnkForExe.TryGetValue(exe, out lnk))
                    LnkForExe[exe] = lnk = FindShortcutForExe(exe);
            }
            if (lnk == null) return;

            dynamic link = ShellCom.CreateShortcut(lnk);
            int want = maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
            if ((int)link.WindowStyle != want)
            {
                link.WindowStyle = want;
                link.Save();
                Log($"shortcut {Path.GetFileName(lnk)} Run -> {(maximized ? "maximized" : "normal")}");
            }
        }
        catch (Exception ex)
        {
            Log("shortcut sync failed: " + ex.Message);
        }
    }

    private static string? FindShortcutForExe(string exe)
    {
        foreach (var dir in ShortcutDirs())
        {
            foreach (var lnk in SafeEnumerateLnks(dir))
            {
                try
                {
                    dynamic link = ShellCom.CreateShortcut(lnk);
                    string target = ((string)link.TargetPath).Trim();
                    // Match by target file name: the key only stores the exe name,
                    // not its folder. ponytail: name-only match; a same-named exe in
                    // two folders picks whichever shortcut is found first.
                    if (string.Equals(Path.GetFileName(target), exe, StringComparison.OrdinalIgnoreCase))
                        return lnk;
                }
                catch { } // unreadable .lnk: skip it
            }
        }
        return null;
    }

    private static IEnumerable<string> ShortcutDirs()
    {
        foreach (var d in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        })
        {
            if (Directory.Exists(d)) yield return d;
        }
    }

    private static IEnumerable<string> SafeEnumerateLnks(string dir)
    {
        // ponytail: IgnoreInaccessible skips locked subfolders instead of aborting
        // the whole scan (the old try/catch lost every lnk before the error).
        try
        {
            return Directory.EnumerateFiles(dir, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
        }
        catch { return Enumerable.Empty<string>(); }
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
                        string tmp = _logPath + ".tmp";
                        File.WriteAllText(tmp, text);
                        File.Move(tmp, _logPath, true);
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
                var path = Path.Combine(dir, "WindowMaximizer.log");
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
