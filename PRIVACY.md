# RelayLoop privacy statement

RelayLoop is local-only software. It contains no telemetry, analytics, advertising, account, cloud-sync, update-check, or network-request feature. Recording, playback, editing, validation, saving, recovery, and runner export all occur on the local Windows computer.

## Data RelayLoop handles

A macro can contain:

- keyboard virtual-key and scan codes with key-down/key-up state;
- pointer coordinates, buttons, and wheel deltas;
- delays between events;
- monitor bounds, device names, primary-display state, and effective DPI;
- the macro creation timestamp.

RelayLoop records key events, not decoded typed strings. A macro can nevertheless reproduce what was typed, so `.rloop` files and exported runner executables should be treated as sensitive user data.

## Sensitive-input protection

RelayLoop does not record on a different Windows input desktop and skips input while the focused control is identified as a password field through standard Win32 or UI Automation metadata. Focus inspection never reads a control's value, name, or typed content. If inspection fails or has not caught up with a focus change, capture fails closed and skips the event.

Custom third-party credential controls may not expose reliable password metadata. Do not record while entering passwords, payment information, recovery codes, private messages, or other secrets.

## Local storage

User-selected `.rloop` files and exported `.exe` runners are written only to destinations the user chooses. RelayLoop also uses:

```text
%LOCALAPPDATA%\RelayLoop\settings.json
%LOCALAPPDATA%\RelayLoop\settings.json.bak (when an older settings version is replaced)
%LOCALAPPDATA%\RelayLoop\Recovery\last-recording.rloop.recovery
%LOCALAPPDATA%\RelayLoop\Logs\relayloop-YYYYMMDD.jsonl
```

- Settings contain UI preferences, shortcut definitions, window position, playback choices, and the most recent macro path.
- Recovery contains the latest recoverable in-progress recording and display metadata. RelayLoop offers to load or discard it after an interrupted session.
- Structured logs contain timestamps, operation names, and exception type names/HResults. The logger API deliberately does not accept free-form user text, typed text, file names, key codes, scan codes, pointer coordinates, or macro event payloads.

RelayLoop does not automatically delete normal macro files or exported runners. A user can remove those files and the `%LOCALAPPDATA%\RelayLoop` folder with ordinary Windows file-management tools when RelayLoop is closed.

## Sharing and exported runners

An exported standalone runner contains the entire macro payload inside the executable. Anyone who receives it may be able to inspect or run those recorded actions. Review the macro and choose recipients carefully. RelayLoop does not upload or transmit exported files.

## Windows boundaries

RelayLoop runs without administrator privileges and does not bypass UAC, secure desktop, UIPI, anti-cheat, protected processes, or other Windows security/input restrictions. `SendInput` cannot control an elevated application from a non-elevated RelayLoop process.
