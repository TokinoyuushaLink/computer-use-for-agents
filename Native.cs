using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace ComputerUse;

internal enum MouseButton { Left, Right, Middle }

internal sealed record WindowInfo(IntPtr Hwnd, uint Pid, string Process, string Title);

/// <summary>Thin Win32 layer: window queries, focus, and synthetic mouse / keyboard input.</summary>
internal static class Native
{
    public const ushort VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkControl = 0x11, VkMenu = 0x12, VkA = 0x41;

    private const int SwRestore = 9;
    private const int DwmwaExtendedFrameBounds = 9, DwmwaCloaked = 14;
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint KeyExtended = 0x1, KeyUp = 0x2, KeyUnicode = 0x4;
    private const uint MouseLeftDown = 0x2, MouseLeftUp = 0x4, MouseRightDown = 0x8, MouseRightUp = 0x10;
    private const uint MouseMiddleDown = 0x20, MouseMiddleUp = 0x40, MouseWheel = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mouse;
        [FieldOffset(0)] public KeyInput key;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public InputUnion data; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short VkKeyScan(char c);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetRect(IntPtr hwnd, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetInt(IntPtr hwnd, int attribute, out int value, int size);

    // ---- windows -------------------------------------------------------------------------------

    /// <summary>Visible, titled, non-cloaked top-level windows (dialogs included).</summary>
    public static List<WindowInfo> EnumerateWindows()
    {
        var windows = new List<WindowInfo>();
        var processNames = new Dictionary<uint, string>();
        EnumWindows((hwnd, _) =>
        {
            if (IsAppWindow(hwnd) && Describe(hwnd, processNames) is { } info)
                windows.Add(info);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool IsAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
            return false;

        // Suspended UWP apps keep an invisible "cloaked" window around; skip those ghosts.
        if (DwmGetInt(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        return className.ToString() != "Progman"; // the desktop
    }

    public static WindowInfo? Describe(IntPtr hwnd, Dictionary<uint, string>? processNames = null)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return null;

        var title = new StringBuilder(length + 1);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        return new WindowInfo(hwnd, pid, ProcessName(pid, processNames), title.ToString());
    }

    private static string ProcessName(uint pid, Dictionary<uint, string>? cache)
    {
        if (cache is not null && cache.TryGetValue(pid, out var cached))
            return cached;

        string name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch
        {
            name = ""; // the process may exit while we enumerate
        }

        if (cache is not null)
            cache[pid] = name;
        return name;
    }

    /// <summary>Visible frame bounds (without the invisible resize border on Windows 10/11).</summary>
    public static Rectangle GetRect(IntPtr hwnd)
    {
        if (DwmGetRect(hwnd, DwmwaExtendedFrameBounds, out var rect, Marshal.SizeOf<NativeRect>()) != 0)
            GetWindowRect(hwnd, out rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>Restore if minimized and bring to the foreground.</summary>
    public static void Focus(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);

        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd)
            return;

        // Foreground lock: a synthetic Alt tap lets SetForegroundWindow succeed.
        Send(KeyEvent(VkMenu, 0, 0), required: false);
        Send(KeyEvent(VkMenu, 0, KeyUp), required: false);
        SetForegroundWindow(hwnd);
    }

    // ---- mouse ---------------------------------------------------------------------------------

    public static void MoveCursor(Point p)
    {
        if (!SetCursorPos(p.X, p.Y))
            throw AppError.Failed($"could not move the cursor to {p.X},{p.Y}");
    }

    public static void MouseDown(MouseButton button) =>
        Send(MouseEvent(button switch { MouseButton.Right => MouseRightDown, MouseButton.Middle => MouseMiddleDown, _ => MouseLeftDown }));

    public static void MouseUp(MouseButton button) =>
        Send(MouseEvent(button switch { MouseButton.Right => MouseRightUp, MouseButton.Middle => MouseMiddleUp, _ => MouseLeftUp }));

    public static void Click(MouseButton button, int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            MouseDown(button);
            Thread.Sleep(30);
            MouseUp(button);
            if (i < count - 1)
                Thread.Sleep(60);
        }
    }

    /// <summary>Positive scrolls up, negative scrolls down (one notch = 120 units).</summary>
    public static void Wheel(int notches) => Send(MouseEvent(MouseWheel, unchecked((uint)(notches * 120))));

    // ---- keyboard ------------------------------------------------------------------------------

    /// <summary>Press all keys in order, then release them in reverse (a chord such as Ctrl+Shift+S).</summary>
    public static void Chord(params ushort[] keys)
    {
        foreach (var key in keys)
        {
            Send(KeyEvent(key, 0, ExtendedFlag(key)));
            Thread.Sleep(10);
        }
        for (var i = keys.Length - 1; i >= 0; i--)
        {
            Send(KeyEvent(keys[i], 0, ExtendedFlag(keys[i]) | KeyUp));
            Thread.Sleep(10);
        }
    }

    /// <summary>Unicode injection: works for any text (CJK included), independent of the keyboard layout.</summary>
    public static void TypeText(string text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '\r': continue;
                case '\n': Chord(VkReturn); continue;
                case '\t': Chord(VkTab); continue;
            }
            Send(KeyEvent(0, c, KeyUnicode));
            Send(KeyEvent(0, c, KeyUnicode | KeyUp));
            Thread.Sleep(3);
        }
    }

    /// <summary>Virtual-key code for a printable character on the current layout, or null.</summary>
    public static ushort? VkFromChar(char c)
    {
        var result = VkKeyScan(c);
        return result == -1 ? null : (ushort)(result & 0xFF);
    }

    // ---- SendInput plumbing --------------------------------------------------------------------

    private static uint ExtendedFlag(ushort vk) =>
        (vk is (>= 0x21 and <= 0x28) or 0x2D or 0x2E) ? KeyExtended : 0; // arrows, Home/End, PgUp/PgDn, Ins, Del

    private static Input KeyEvent(ushort vk, ushort scan, uint flags) => new()
    {
        type = InputKeyboard,
        data = new InputUnion { key = new KeyInput { wVk = vk, wScan = scan, dwFlags = flags } }
    };

    private static Input MouseEvent(uint flags, uint data = 0) => new()
    {
        type = InputMouse,
        data = new InputUnion { mouse = new MouseInput { dwFlags = flags, mouseData = data } }
    };

    private static void Send(Input input, bool required = true)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) == 1 || !required)
            return;

        // UIPI: input into an elevated window is silently dropped unless we are elevated too.
        throw AppError.Failed(
            $"input was blocked (Win32 error {Marshal.GetLastWin32Error()}); the target window may be elevated - run computerUse as administrator");
    }
}

/// <summary>Key names accepted by the "key" command, e.g. "ctrl+shift+s".</summary>
internal static class KeyNames
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["capslock"] = 0x14
    };

    public static ushort[] ParseChord(string chord) => chord.Split('+').Select(Parse).ToArray();

    private static ushort Parse(string raw)
    {
        var name = raw.Trim();
        if (Named.TryGetValue(name, out var vk))
            return vk;

        if (name.Length > 1 && (name[0] is 'f' or 'F') && int.TryParse(name[1..], out var n) && n is >= 1 and <= 24)
            return (ushort)(0x6F + n); // F1 = 0x70

        if (name.Length == 1 && Native.VkFromChar(name[0]) is { } ch)
            return ch;

        throw AppError.BadArgs($"unknown key: {raw}");
    }
}
