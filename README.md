# Keyboard Chaos (Windows)

This native companion remaps `A–Z` to the reverse alphabet and reverses mouse movement across Windows. It registers itself for the current user's sign-in and shows a warning tray icon while active.

Emergency stop: `Ctrl+Alt+Shift+F12`. This ends the current session immediately and restores ordinary input. The tray icon also exposes **Force stop now** and **Disable startup and exit**. The floating `×` moves away at most five times; after that, it stays put so you can click it to exit.

To manually force-stop it from a terminal:

```powershell
.\KeyboardChaos.exe --force-stop
```

The app does not intercept modified keyboard shortcuts (`Ctrl`, `Alt`, or Windows key combinations), so normal recovery paths remain available.

## Floating icon settings

Edit `KeyboardChaos.settings.json` beside `KeyboardChaos.exe`, then restart the app. For example:

```json
{
  "EscapeAttempts": 5,
  "EscapeCheckMilliseconds": 90,
  "RecalibrateEverySeconds": 20
}
```

`EscapeAttempts` is the number of times the floating `×` moves away before staying still. Set it to `0` to keep the icon still immediately.
`RecalibrateEverySeconds` controls how often the pointer is forcibly returned to the virtual-screen center.
