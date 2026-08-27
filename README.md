# RelayLoop

RelayLoop is a compact, offline Windows macro recorder built as an original clean-room implementation in C# with .NET 8 and WPF. It records global keyboard and pointer input with monotonic timing, replays it through `SendInput`, edits recordings, and exports confirmed standalone runners. RelayLoop does not use TinyTask branding, assets, binaries, or file formats.

> **Development status:** RelayLoop is a work in progress. Features and the macro format may continue to change while the project is being updated.

## Run the portable build

The published x64 build is in `artifacts\win-x64\RelayLoop-portable`. Launch:

```powershell
.\artifacts\win-x64\RelayLoop-portable\RelayLoop.exe
```

The folder is self-contained: a separate .NET installation is not required. Keep the `RunnerStub` subfolder beside `RelayLoop.exe` if you want standalone runner export to work.

RelayLoop runs as the current user. It does not request elevation, install a service, add itself to Windows startup, or create an account.

## Toolbar and controls

The compact toolbar is ordered as follows:

`Open | Save | Record | Play | Stop | Export | Repeat | Speed | Settings`

- Open loads a `.rloop` file. You can also drop one `.rloop` file onto the window.
- Save writes the current macro atomically. `Ctrl+Shift+S` opens Save As.
- Record starts or stops global capture. The record control and status indicator turn red while capture is active.
- Play replays enabled events at the selected speed and repeat count. The play control and status indicator turn green while active.
- Stop immediately cancels a countdown, recording, or playback and releases inputs held by playback.
- Export creates a single-file `.exe` containing the loaded macro and the RelayLoop runner.
- Repeat accepts `1` through `9999`. It is disabled while continuous playback is selected.
- Speed includes `0.25`, `0.5`, `1`, `2`, `4`, and `8`; any finite custom value from `0.25` through `100` is accepted.
- Settings controls the theme, always-on-top mode, optional three-second countdown, continuous playback, and global hotkeys.
- The chevron or `Ctrl+E` opens the event inspector and activity panel.

Keyboard shortcuts inside the window:

| Action | Shortcut |
|---|---|
| Open | `Ctrl+O` |
| Save | `Ctrl+S` |
| Save As | `Ctrl+Shift+S` |
| Record / stop recording | `F2` |
| Play | `F3` |
| Stop or cancel countdown | `Esc` |
| Expanded mode | `Ctrl+E` |
| Undo / redo editor change | `Ctrl+Z` / `Ctrl+Y` |
| Delete selected event | `Delete` |

Default global hotkeys work even when RelayLoop is not focused:

| Action | Default global hotkey |
|---|---|
| Start / stop recording | `Ctrl+Shift+Alt+R` |
| Start playback | `Ctrl+Shift+Alt+P` |
| Emergency stop | `Ctrl+Shift+Alt+S` |

Global shortcuts must use at least one modifier plus a supported letter, number, function key, navigation key, Space, Tab, Insert, or Delete. RelayLoop checks for duplicates and Windows registration conflicts. Playback remains disabled unless the emergency-stop hotkey was registered successfully.

## Typical workflow

1. Put the target applications in the desired positions.
2. Select Record. If enabled, the three-second countdown gives you time to focus the target.
3. Perform the actions, then use the record shortcut or Stop.
4. Open expanded mode to inspect, enable/disable, edit, delete, or reorder events. Editor changes support undo and redo.
5. Select a speed, repeat count, or continuous playback.
6. Select Play. Review any monitor-layout warning before continuing.
7. Use Stop or the emergency hotkey at any time.
8. Save the macro or export a runner.

The most recently opened or saved macro is loaded on the next start if it is still available. Window position, playback choices, hotkeys, theme, expanded state, countdown, and always-on-top preferences are also restored.

## Standalone runners

Export appends a validated `.rloop` payload and SHA-256 footer to a self-contained x64 runner stub. The result is one portable Windows executable. Every exported runner:

- opens a visible confirmation window;
- describes its event count, duration, and display-layout status;
- requires the user to check an explicit confirmation box before playback;
- registers `Ctrl+Shift+Alt+S` before enabling playback;
- refuses playback if that emergency hotkey is unavailable;
- releases tracked keys and mouse buttons on completion, cancellation, error, and close.

An exported runner plays once at the original recorded timing. Speed, repeat, and continuous settings belong to the main application and are not embedded in format version 1.

## Safety and platform limitations

- Macros send real keyboard and pointer input. Review unfamiliar recordings before running them.
- `SendInput` is subject to Windows User Interface Privilege Isolation (UIPI). A non-elevated RelayLoop process cannot control an elevated application. RelayLoop intentionally does not request administrator privileges or bypass that boundary.
- RelayLoop cannot automate the UAC/secure desktop, Windows sign-in, or other protected desktops.
- Capture fails closed while the focused control cannot be safely inspected. Input over standard or UI Automation password fields is skipped. This protection does not make arbitrary third-party custom controls trustworthy; do not record while secrets are being entered.
- Low-level hooks and `SendInput` can be blocked by Windows policy, endpoint security, remote-session behavior, sandboxing, or the target application's input model.
- Games and third-party services may prohibit macros. RelayLoop does not bypass anti-cheat or input restrictions; using it can violate service rules.
- Monitor coordinates are physical pixels across the complete Windows virtual desktop, including negative coordinates. A changed monitor arrangement, resolution, primary display, or DPI can move pointer targets. RelayLoop warns before playback but does not remap the macro automatically.
- RelayLoop retries release events that Windows rejects and blocks new playback while unresolved releases remain. If Windows continues to reject cleanup, RelayLoop displays a warning; press and release the affected physical keys or mouse buttons manually.

Never leave a macro unattended until you have verified its behavior. Keep the emergency-stop shortcut available.

## Recovery and local files

During recording, RelayLoop periodically saves a validated recovery copy. At the next launch, an interrupted recording can be loaded or explicitly discarded. Normal saves use a temporary file plus an atomic replace/move so an incomplete write does not overwrite the prior macro.

Application-local data is stored under:

```text
%LOCALAPPDATA%\RelayLoop\
  settings.json
  settings.json.bak (created after later atomic settings updates)
  Recovery\last-recording.rloop.recovery
  Logs\relayloop-YYYYMMDD.jsonl
```

Structured logs contain timestamps, operation names, exception types, and non-sensitive status. They never contain typed text, virtual-key codes, scan codes, pointer coordinates, or recorded event payloads. See [PRIVACY.md](PRIVACY.md).

## `.rloop` macro format, version 1

`.rloop` is RelayLoop's original UTF-8 JSON format. Property names and enum values are case-sensitive. Unknown, missing, malformed, oversized, or unsupported data is rejected before playback.

Example:

```json
{
  "format": "RelayLoop.Macro",
  "version": 1,
  "createdUtc": "2026-08-26T12:00:00+00:00",
  "displayLayout": {
    "virtualLeft": -1920,
    "virtualTop": 0,
    "virtualWidth": 3840,
    "virtualHeight": 1080,
    "monitors": [
      {
        "deviceName": "\\\\.\\DISPLAY1",
        "left": 0,
        "top": 0,
        "width": 1920,
        "height": 1080,
        "dpiX": 96,
        "dpiY": 96,
        "isPrimary": true
      },
      {
        "deviceName": "\\\\.\\DISPLAY2",
        "left": -1920,
        "top": 0,
        "width": 1920,
        "height": 1080,
        "dpiX": 120,
        "dpiY": 120,
        "isPrimary": false
      }
    ]
  },
  "events": [
    {
      "kind": "mouseMove",
      "delayMicroseconds": 250000,
      "enabled": true,
      "x": -400,
      "y": 300,
      "button": "none",
      "wheelDelta": 0,
      "isHorizontalWheel": false,
      "virtualKey": 0,
      "scanCode": 0,
      "isExtendedKey": false
    },
    {
      "kind": "keyDown",
      "delayMicroseconds": 50000,
      "enabled": true,
      "x": 0,
      "y": 0,
      "button": "none",
      "wheelDelta": 0,
      "isHorizontalWheel": false,
      "virtualKey": 65,
      "scanCode": 30,
      "isExtendedKey": false
    }
  ]
}
```

Event kinds are `mouseMove`, `mouseButtonDown`, `mouseButtonUp`, `mouseWheel`, `keyDown`, and `keyUp`. Mouse buttons are `none`, `left`, `right`, `middle`, `x1`, and `x2`. `delayMicroseconds` is the monotonic delay since the preceding event. Disabled events are not injected, but their delay remains part of the timeline. Horizontal wheel events set `isHorizontalWheel` to `true`.

Defensive limits include a 128 MiB file size, 1,000,000 events, 64 monitors, seven days per event delay, bounded coordinates/DPI, a JSON depth of 32, and a maximum of 256 reported validation issues.

## Build and test

Requirements for development:

- Windows 10 or Windows 11 x64
- .NET 8 SDK `8.0.424` (the repository is pinned by `global.json`)
- PowerShell

From the repository root:

```powershell
dotnet restore .\RelayLoop.sln
dotnet build .\RelayLoop.sln --no-restore -c Release -p:Platform=x64
dotnet test .\RelayLoop.sln --no-restore -c Release -p:Platform=x64
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Configuration Release
```

This workspace also supports its local SDK explicitly:

```powershell
.\.tools\dotnet\dotnet.exe test .\RelayLoop.sln -c Release -p:Platform=x64
```

The publish script builds both self-contained single-file executables, packages the runner stub, and copies the documentation into `artifacts\win-x64\RelayLoop-portable`.

Solution layout:

- `src\RelayLoop.Core` — versioned macro model, validation, serialization, timing, history, state machine, and runner payload codec.
- `src\RelayLoop.App` — WPF toolbar/editor, MVVM coordination, hooks, hotkeys, display capture, recovery, settings, themes, logging, playback, and export.
- `src\RelayLoop.Runner` — visible confirmation runner, embedded-payload reader, emergency stop, layout comparison, and standalone playback.
- `tests\RelayLoop.Core.Tests` — serialization, validation, timing, repeats, cancellation, state, history, and payload tests.
- `tests\RelayLoop.IntegrationTests` — recorder/player sequences, input cleanup, permission failures, hotkey conflicts, export, runner playback, and multi-monitor/DPI behavior.

## Troubleshooting

**A hotkey says it is already in use.** Another application—or another shortcut in RelayLoop—owns the same combination. Open Settings, enter a different shortcut, and select Apply hotkeys. Playback remains unavailable if the stop shortcut cannot be registered.

**An elevated app does not respond.** This is expected UIPI behavior. A non-elevated process cannot use `SendInput` to control an elevated target. RelayLoop will not elevate itself or bypass Windows security boundaries.

**Recording skips input.** Focus may be on a password/credential field, a protected desktop, or a control RelayLoop cannot safely inspect. Endpoint policy can also block low-level hooks.

**Pointer actions land in the wrong place.** Restore the recorded monitor arrangement, resolution, primary display, scaling, and application window positions. Review the display-layout warning before playback.

**Export reports a missing runner stub.** Keep `RunnerStub\RelayLoop.Runner.exe` beside the published `RelayLoop.exe`, or set `RELAYLOOP_RUNNER_STUB` to a published x64 runner during development.

**A key appears held after an error.** Use the emergency stop, then physically press and release the affected key or mouse button. This is only expected when Windows itself repeatedly rejects `SendInput` release packets.

**The app will not start twice.** RelayLoop is intentionally single-instance per Windows session so two processes cannot compete for hooks and global hotkeys.
