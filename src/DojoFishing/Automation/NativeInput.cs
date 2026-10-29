using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace DojoFishing.Automation;

public static class NativeInput
{
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority;
        public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    public static void Click()
    {
        var inputs = new[] {
            new Input { Type = 0, Value = new InputUnion { Mouse = new MouseInput { Flags = 2 } } },
            new Input { Type = 0, Value = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }
        };
        uint sent = SendInput(2, inputs, Marshal.SizeOf<Input>());
        if (sent == 1) SendInput(1, [inputs[1]], Marshal.SizeOf<Input>());
        if (sent != 2) throw new Win32Exception(Marshal.GetLastWin32Error(), "クリック送信に失敗しました。対象アプリの権限も確認してください。");
    }
    public static bool IsTargetForeground(int target)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        if (pid == target) return true;
        var parents = new Dictionary<uint, uint>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return false;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
            if (Process32FirstW(snapshot, ref entry))
                do { parents[entry.Pid] = entry.Parent; } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        var visited = new HashSet<uint>();
        while (visited.Add(pid) && parents.TryGetValue(pid, out pid)) if (pid == target) return true;
        return false;
    }
    public static (uint Modifiers, uint Key) ParseHotkey(string text)
    {
        uint mods = 0x4000;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) throw new ArgumentException("ホットキーは Ctrl+Alt+F10 の形式で指定してください。");
        foreach (string token in tokens[..^1])
            mods |= token.ToUpperInvariant() switch { "CTRL" => 2u, "ALT" => 1u, "SHIFT" => 4u, _ => throw new ArgumentException("修飾キーはCtrl/Alt/Shiftです。") };
        if (!Enum.TryParse<Key>(tokens[^1], true, out var key) || !Enum.IsDefined(key) || key == Key.None || KeyInterop.VirtualKeyFromKey(key) == 0)
            throw new ArgumentException("ホットキーのキー名が不正です。");
        return (mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
}
