using System.ComponentModel;
using System.Runtime.InteropServices;
using Spectre.Console;

namespace CodeAssist.Tui.FullScreen;

public readonly record struct WorkspaceInputEvent(ConsoleKeyInfo? Key, int Column = -1, int Row = -1, int Scroll = 0);

public interface IWorkspaceInput : IDisposable
{
    ValueTask<WorkspaceInputEvent?> ReadAsync(CancellationToken cancellation);
}

public sealed class WorkspaceTerminalInput : IWorkspaceInput
{
    private readonly IAnsiConsole console;
    private readonly INativeApi? api;
    private readonly uint originalMode;
    private readonly bool native;
    private bool pressed;
    private bool disposed;

    public WorkspaceTerminalInput(IAnsiConsole console, INativeApi? api = null)
    {
        this.console = console;
        if (api is null && (!OperatingSystem.IsWindows() || Console.IsInputRedirected ||
            console.Input.GetType().Assembly != typeof(AnsiConsole).Assembly)) return;
        this.api = api ?? new WindowsApi();
        if (!this.api.TryGetMode(out originalMode)) return;
        var mode = (originalMode | 0x0080u | 0x0010u | 0x0008u) & ~(0x0040u | 0x0002u | 0x0004u | 0x0001u | 0x0200u);
        if (!this.api.SetMode(mode)) throw new Win32Exception(Marshal.GetLastWin32Error());
        native = true;
        try { console.Clear(); }
        catch { this.api.SetMode(originalMode); throw; }
    }

    public async ValueTask<WorkspaceInputEvent?> ReadAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!native)
        {
            var key = await console.Input.ReadKeyAsync(true, cancellation);
            return key is null ? null : new WorkspaceInputEvent(key);
        }
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = api!.PendingCount();
            if (count == 0) { await Task.Delay(16, cancellation); continue; }
            var record = api.Read();
            if (record.Type == 1 && record.KeyDown != 0)
                return new(new ConsoleKeyInfo(record.Character, (ConsoleKey)record.VirtualKey,
                    (record.KeyModifiers & 0x10) != 0, (record.KeyModifiers & 0x03) != 0, (record.KeyModifiers & 0x0c) != 0));
            if (record.Type != 2) continue;
            var scroll = record.MouseFlags == 4 ? -Math.Sign(unchecked((short)(record.Buttons >> 16))) : 0;
            var down = (record.Buttons & 1) != 0;
            var clicked = down && !pressed && record.MouseFlags == 0;
            pressed = down;
            if (!clicked && scroll == 0) continue;
            return new(null, record.MouseX, record.MouseY - api.WindowTop(), scroll);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (native) api!.SetMode(originalMode);
    }

    public interface INativeApi
    {
        bool TryGetMode(out uint mode);
        bool SetMode(uint mode);
        uint PendingCount();
        InputRecord Read();
        int WindowTop();
    }

    private sealed class WindowsApi : INativeApi
    {
        private readonly IntPtr handle = GetStdHandle(-10);
        public bool TryGetMode(out uint mode) => GetConsoleMode(handle, out mode);
        public bool SetMode(uint mode) => SetConsoleMode(handle, mode);
        public uint PendingCount()
        {
            if (!GetNumberOfConsoleInputEvents(handle, out var count)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return count;
        }
        public InputRecord Read()
        {
            if (!ReadConsoleInput(handle, out var record, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return record;
        }
        public int WindowTop() => GetConsoleScreenBufferInfo(GetStdHandle(-11), out var screen) ? screen.Window.Top : 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct InputRecord
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(14)] public char Character;
        [FieldOffset(16)] public uint KeyModifiers;
        [FieldOffset(4)] public short MouseX;
        [FieldOffset(6)] public short MouseY;
        [FieldOffset(8)] public uint Buttons;
        [FieldOffset(16)] public uint MouseFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect { public short Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenInfo
    {
        public short SizeX, SizeY, CursorX, CursorY;
        public ushort Attributes;
        public SmallRect Window;
        public short MaximumX, MaximumY;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNumberOfConsoleInputEvents(IntPtr handle, out uint count);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadConsoleInput(IntPtr handle, out InputRecord record, uint length, out uint read);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out ScreenInfo info);
}