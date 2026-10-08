# WindowMaximizer

A small Windows tray app that remembers whether each window was **closed maximized**
and re-maximizes it the next time it opens, and remembers the **position/size**
of windows closed normal. Works for classic Win32 windows
(Notepad, Explorer, Chrome, ...) and UWP apps (Calculator, Settings, ...).

Windows closed maximized reopen maximized; windows closed normal reopen with
their saved position/size. A window that is **minimized from maximized** still counts as
maximized, and if a new window of the same app opens while a sibling is
maximized (or minimized from maximized), the new one comes up maximized too —
so maximize a folder, minimize it, and reopen it and it stays maximized.

Once a window is maximized, it **stays maximized** through minimize + reopen —
no time limit: re-activating it (reopening the folder, or the window being
restored) brings it back maximized no matter how long ago the maximize was.
The only thing that ends that intent is an explicit **Restore** (un-maximizing
a maximized window); after that the app leaves the window alone.

**State is remembered per window**, not per app: each Explorer folder keeps its
own state (two folders can differ — one maximized, one normal), and every UWP
app is separate. On a **display change** (resolution change, monitor
plugged/unplugged), windows that were maximized are re-maximized against the
new work area, since Windows shrinks them; minimized windows are left alone.

## Usage

Run `WindowMaximizer.exe` — it lives in the system tray (no console window).
Keep it running; it must be alive while windows open and close to record and
restore state. Right-click the tray icon for the menu:

- **Start with Windows (elevated)** — check/uncheck to enable or disable
  auto-start at sign-in. Creates a scheduled task (`WindowMaximizer`, logon
  trigger, highest privileges) that starts the app **as administrator with no
  UAC prompt** — see below for why that matters.
- **Restart as administrator** — needed for apps that run elevated (see below).
- **Exit** — closes the app.

### Elevated windows (e.g. Task Manager)

On Windows 11, Task Manager runs at a higher privilege level, and Windows
silently refuses window changes from non-admin apps. So the watcher can record
that Task Manager closed maximized but **cannot restore it unless it is itself
running as administrator**. If a restore is blocked you'll get a tray
notification — choose **Restart as administrator** from the menu. This applies
to any window of an elevated process.

Auto-start is implemented as a scheduled task with `RunLevel=HighestAvailable`,
so at sign-in the app is already elevated — no UAC prompt, no need to manually
restart as admin. (The task replaces the old `HKCU\...\Run` value; enabling
removes the legacy value, and disabling deletes the task.)

Headless autostart control (same code path as the menu):

```
WindowMaximizer.exe --autostart on|off     # exit code 0 = ok
WindowMaximizer.exe --autostart query      # exit code 0 = enabled, 1 = disabled
```

Activity is logged to `WindowMaximizer.log` next to the exe (falls back to
`%LOCALAPPDATA%` if the exe's folder isn't writable). `--verbose` logs every
window every poll plus a periodic heartbeat with watcher-pass and census-scan
timings and managed-heap size.

## How it works

- **Event-driven detection with a polling safety net.** A `SetWinEventHook`
  wakes the watcher the moment a window is created/shown/hidden/destroyed or
  toggles maximize/minimize, so restores react within milliseconds. A 2-second
  fallback poll catches any events Windows drops under load. Idle CPU is
  ~0.15% of one core (measured).
- On each wake it enumerates top-level windows (visible, not cloaked, not owned,
  not tool windows) and reads each window's placement (`GetWindowPlacement`).
- When a window disappears, its last state is recorded in the registry:
  `HKCU\Software\WindowMaximizer` (one DWORD per window key
  `<exe>|<class>|<title>`, or `<exe>|<class>` when the window has no title).
  A normal close also records its position/size (`left,top,right,bottom` string
  under the SHA-1 of `<key>|bounds`), restored with `SetWindowPlacement` on the
  next open (skipped when off-screen after a monitor change; a minimized window
  keeps its minimized state). Each titled window gets its own key so two Explorer folders or two Chrome
  windows keep separate state. Registry value names are SHA-1 hex digests of
  the key string (registry value names cannot contain backslashes). State
  survives app restarts.
- **Same-title windows are disambiguated per instance**: the first window of a
  given key is stored under the plain key, the second under `|#2`, the third
  under `|#3`, and so on, so two windows titled "Untitled - Notepad" (or two
  windows that momentarily share a title) each keep their own state — closing
  the first maximized doesn't make the second reopen maximized. Instance slots
  are freed when their window closes, so a new window of the same key takes
  over the freed slot (the one that was closed maximized, if any).
- The class key is **never** used as a fallback for a titled window: it is
  shared by every window of that class (two Explorer folders, or two Steam
  dialogs), so falling back to it would merge per-window states back together.
  Titled windows restore from their title key only; untitled windows use the
  class key (which is their key anyway).
- When a window appears whose key was recorded as closed-maximized, it is
  maximized via `SetWindowPlacement` (no focus steal); when recorded as
  closed-normal with saved bounds, its position/size is restored.
- Closes are grouped per key (an app like Win11 Notepad owns several visible
  windows at once), and a short "recently maximized" history covers UWP's
  teardown animation, which briefly reverts the frame to normal on close.
- Window text is read with `SendMessageTimeout` (never `GetWindowText`, which
  blocks forever on a hung window), so a hung window anywhere on the system
  can never block the watcher. (Explorer first shows a generic "File Explorer"
  title, then the folder name; a window whose key changes is re-evaluated
  against the new key so the restore isn't missed.)
- A `WM_DISPLAYCHANGE` message filter wakes the watcher on display changes
  (resolution, monitor plug/unplug) to re-maximize shrunk windows.

## Build

Requires the .NET 10 SDK:

```
cd WindowMaximizer
dotnet build -c Release
```

The exe lands at `WindowMaximizer/bin/Release/net10.0-windows/WindowMaximizer.exe`.

The icon (`app.ico`, 16/24/32/48/256 px) is embedded in the exe and used as the
tray icon. Regenerate it with `powershell -File gen-icon.ps1` if you want a
different look.

## Test

`test.ps1` drives real apps the way Task Manager would (maximize → End Task →
reopen) and asserts the behavior:

```
powershell -ExecutionPolicy Bypass -File test.ps1
```

- Classic window: Notepad — maximize, End Task (`taskkill /F`), reopen → must
  come back maximized. Then close normal → reopen → must NOT be maximized.
- UWP window: Calculator — same flow through its `ApplicationFrameWindow`.

`-SkipClassic` / `-SkipUwp` / `-Discover` limit the run.
