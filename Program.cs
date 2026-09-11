using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace KeyboardChaos;

internal static class Program
{
    private const string StopEventName = "Local\\KeyboardChaosForceStop";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--force-stop", StringComparer.OrdinalIgnoreCase))
        {
            try { EventWaitHandle.OpenExisting(StopEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        using var instance = new Mutex(true, "Local\\KeyboardChaosSingleInstance", out var first);
        if (!first) return;
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        ApplicationConfiguration.Initialize();
        using var context = new ChaosContext(stopEvent);
        Application.Run(context);
    }
}

internal sealed class ChaosContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "KeyboardChaos";
    private const uint WhKeyboardLl = 13, WhMouseLl = 14, LlkhfInjected = 0x10;
    private const int HcAction = 0, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmMouseMove = 0x0200;

    private readonly EventWaitHandle _stopEvent;
    private readonly HookProc _keyboardProc;
    private readonly NotifyIcon _trayIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly FloatingStopForm _floatingIcon;
    private readonly System.Threading.Timer _recalibrationTimer;
    private readonly SynchronizationContext _uiContext;
    private IntPtr _keyboardHook;
    private NativePoint _lastMouse;
    private bool _stopped;

    public ChaosContext(EventWaitHandle stopEvent)
    {
        _stopEvent = stopEvent;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        EnableStartup();
        _keyboardProc = KeyboardHook;
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
        _lastMouse = new NativePoint
        {
            X = GetSystemMetrics(76) + GetSystemMetrics(78) / 2,
            Y = GetSystemMetrics(77) + GetSystemMetrics(79) / 2,
        };
        SetCursorPos(_lastMouse.X, _lastMouse.Y);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Force stop now (Ctrl+Alt+Shift+F12)", null, (_, _) => ForceStop());
        menu.Items.Add("Disable startup and exit", null, (_, _) => { DisableStartup(); ForceStop(); });
        _trayIcon = new NotifyIcon { Icon = SystemIcons.Warning, Text = "Keyboard Chaos active — Ctrl+Alt+Shift+F12 stops it", ContextMenuStrip = menu, Visible = true };
        _hotkeyWindow = new HotkeyWindow(ForceStop, InvertMouseFromRawInput);
        var settings = ChaosSettings.Load();
        _floatingIcon = new FloatingStopForm(ForceStop, settings);
        _floatingIcon.Show();
        _recalibrationTimer = new System.Threading.Timer(_ => RecalibrateMouseToCenter(), null, settings.RecalibrateEverySeconds * 1000, settings.RecalibrateEverySeconds * 1000);
        ThreadPool.RegisterWaitForSingleObject(_stopEvent, (_, _) => _uiContext.Post(_ => ForceStop(), null), null, -1, true);
    }

    private static void EnableStartup()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application executable was not found.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        key.SetValue(RunValueName, $"\"{executable}\"");
    }
    private static void DisableStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        key.DeleteValue(RunValueName, false);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HcAction || (wParam.ToInt32() != WmKeyDown && wParam.ToInt32() != WmSysKeyDown)) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((data.Flags & LlkhfInjected) != 0 || ModifierPressed()) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        if (data.VkCode is >= 0x41 and <= 0x5A)
        {
            SendKey((ushort)(0x5A - (int)(data.VkCode - 0x41)));
            return (IntPtr)1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void InvertMouseFromRawInput()
    {
        if (_stopped || !GetCursorPos(out var point)) return;
        if (point.X == _lastMouse.X && point.Y == _lastMouse.Y) return;
        var next = new NativePoint { X = _lastMouse.X - (point.X - _lastMouse.X), Y = _lastMouse.Y - (point.Y - _lastMouse.Y) };
        next.X = Math.Clamp(next.X, GetSystemMetrics(76), GetSystemMetrics(76) + GetSystemMetrics(78) - 1);
        next.Y = Math.Clamp(next.Y, GetSystemMetrics(77), GetSystemMetrics(77) + GetSystemMetrics(79) - 1);
        _lastMouse = next;
        SetCursorPos(next.X, next.Y);
    }

    private void RecalibrateMouseToCenter()
    {
        if (_stopped) return;
        var center = new NativePoint
        {
            X = GetSystemMetrics(76) + GetSystemMetrics(78) / 2,
            Y = GetSystemMetrics(77) + GetSystemMetrics(79) / 2,
        };
        _lastMouse = center;
        SetCursorPos(center.X, center.Y);
    }

    private static bool ModifierPressed() => (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
    private static void SendKey(ushort key)
    {
        var inputs = new[] { new Input { Type = 1, Data = new InputUnion { Keyboard = new KeybdInput { VirtualKey = key } } }, new Input { Type = 1, Data = new InputUnion { Keyboard = new KeybdInput { VirtualKey = key, Flags = 2 } } } };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }
    private void ForceStop()
    {
        if (_stopped) return;
        _stopped = true;
        _recalibrationTimer.Dispose();
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        _trayIcon.Visible = false;
        _floatingIcon.Close();
        _hotkeyWindow.Dispose();
        ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _floatingIcon.Dispose(); _trayIcon.Dispose(); _hotkeyWindow.Dispose(); }
        base.Dispose(disposing);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct KbdLlHookStruct { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MsLlHookStruct { public NativePoint Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeybdInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KeybdInput { public ushort VirtualKey, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(uint id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? name);
}

internal sealed class ChaosSettings
{
    public int EscapeAttempts { get; set; } = 5;
    public int EscapeCheckMilliseconds { get; set; } = 90;
    public int RecalibrateEverySeconds { get; set; } = 20;

    public static ChaosSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "KeyboardChaos.settings.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<ChaosSettings>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    loaded.EscapeAttempts = Math.Clamp(loaded.EscapeAttempts, 0, 100);
                    loaded.EscapeCheckMilliseconds = Math.Clamp(loaded.EscapeCheckMilliseconds, 30, 2000);
                    loaded.RecalibrateEverySeconds = Math.Clamp(loaded.RecalibrateEverySeconds, 1, 3600);
                    return loaded;
                }
            }
        }
        catch (JsonException) { }

        var defaults = new ChaosSettings();
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
        return defaults;
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312, WmInput = 0x00FF;
    private readonly Action _onForceStop;
    private readonly Action _onMouseInput;
    public HotkeyWindow(Action onForceStop, Action onMouseInput)
    {
        _onForceStop = onForceStop;
        _onMouseInput = onMouseInput;
        CreateHandle(new CreateParams());
        RegisterHotKey(Handle, 1, 0x0002 | 0x0001 | 0x0004, 0x7B);
        var mouse = new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = 0x00000100, Target = Handle };
        RegisterRawInputDevices(new[] { mouse }, 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey) _onForceStop();
        base.WndProc(ref message);
        if (message.Msg == WmInput) _onMouseInput();
    }
    public void Dispose() { if (Handle != IntPtr.Zero) { UnregisterHotKey(Handle, 1); DestroyHandle(); } }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
}

internal sealed class FloatingStopForm : Form
{
    private readonly Action _stop;
    private readonly System.Windows.Forms.Timer _proximityTimer;
    private readonly ToolTip _toolTip = new();
    private int _spot;
    private int _attemptsRemaining;
    private static readonly Point[] Spots =
    {
        new(40, 40), new(40, 170), new(40, 300), new(40, 430),
        new(0, 0), new(0, 0), new(0, 0), new(0, 0),
    };

    public FloatingStopForm(Action stop, ChaosSettings settings)
    {
        _stop = stop;
        _attemptsRemaining = settings.EscapeAttempts;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(58, 58);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(181, 42, 53);
        Opacity = 0.96;
        Location = InitialLocation();

        var button = new Button
        {
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = Color.FromArgb(181, 42, 53),
            ForeColor = Color.White,
            Font = new Font(FontFamily.GenericSansSerif, 18, FontStyle.Bold),
            Text = "×",
            TabStop = false,
        };
        button.Click += (_, _) => _stop();
        _toolTip.SetToolTip(button, "Stop Keyboard Chaos\nCtrl+Alt+Shift+F12");
        Controls.Add(button);

        _proximityTimer = new System.Windows.Forms.Timer { Interval = settings.EscapeCheckMilliseconds };
        _proximityTimer.Tick += (_, _) => MoveAwayWhenApproached();
        _proximityTimer.Start();
    }

    private Point InitialLocation()
    {
        var workArea = Screen.PrimaryScreen!.WorkingArea;
        return new Point(workArea.Right - Width - 28, workArea.Top + 28);
    }

    private void MoveAwayWhenApproached()
    {
        if (_attemptsRemaining == 0) return;
        var pointer = Cursor.Position;
        var center = new Point(Left + Width / 2, Top + Height / 2);
        if (Math.Abs(pointer.X - center.X) > 130 || Math.Abs(pointer.Y - center.Y) > 130) return;

        var workArea = Screen.FromPoint(pointer).WorkingArea;
        var candidates = new[]
        {
            new Point(workArea.Left + 28, workArea.Top + 28),
            new Point(workArea.Right - Width - 28, workArea.Top + 28),
            new Point(workArea.Left + 28, workArea.Bottom - Height - 28),
            new Point(workArea.Right - Width - 28, workArea.Bottom - Height - 28),
        };
        _spot = (_spot + 1) % candidates.Length;
        Location = candidates[_spot];
        _attemptsRemaining--;
        if (_attemptsRemaining == 0)
        {
            _proximityTimer.Stop();
            _toolTip.SetToolTip(Controls[0], "Stop Keyboard Chaos\nClick to exit — Ctrl+Alt+Shift+F12 also works");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _proximityTimer.Stop(); _proximityTimer.Dispose(); _toolTip.Dispose(); }
        base.Dispose(disposing);
    }
}
