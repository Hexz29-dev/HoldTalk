using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Holdtalk;

public static class Native
{
    public delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetClipboardData(uint format, nint data);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardData { public uint Key, ScanCode, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }

    public static void Paste()
    {
        Input Key(ushort key, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0 } } };
        Input[] inputs = [Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true)];
        if (SendInput(4, inputs, Marshal.SizeOf<Input>()) != 4)
        {
            SendInput(2, [Key(0x56, true), Key(0x11, true)], Marshal.SizeOf<Input>());
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked paste. The transcript is in history; elevated apps require matching privileges.");
        }
    }
}

// One latch per physical key: repeats and releases never start a second recording.
public sealed class HoldKeys(Keys dictation, Keys cleanup)
{
    private readonly HashSet<Keys> held = [];
    public event Action<Keys, bool>? Changed;

    public bool Handle(Keys key, bool down, bool enabled)
    {
        if (key != dictation && key != cleanup) return false;
        if (down)
        {
            if (held.Contains(key)) return true;
            if (!enabled) return false;
            held.Add(key);
            Changed?.Invoke(key, true);
            return true;
        }
        if (!held.Remove(key)) return false;
        Changed?.Invoke(key, false);
        return true;
    }
}

public sealed class KeyboardHook : IDisposable
{
    private readonly Native.HookCallback callback;
    private readonly nint hook;
    public KeyboardHook(HoldKeys keys, Func<bool> enabled)
    {
        callback = (code, message, pointer) =>
        {
            if (code >= 0 && ((int)message is 0x100 or 0x101 or 0x104 or 0x105))
            {
                var data = Marshal.PtrToStructure<Native.KeyboardData>(pointer);
                if ((data.Flags & 0x10) == 0)
                {
                    Keys key = (Keys)data.Key;
                    if (key == Keys.ControlKey) key = (data.Flags & 1) != 0 ? Keys.RControlKey : Keys.LControlKey;
                    if (key == Keys.ShiftKey) key = data.ScanCode == 0x36 ? Keys.RShiftKey : Keys.LShiftKey;
                    if (keys.Handle(key, (int)message is 0x100 or 0x104, enabled())) return 1;
                }
            }
            return Native.CallNextHookEx(0, code, message, pointer);
        };
        hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the global keyboard hook.");
    }
    public void Dispose() { Native.UnhookWindowsHookEx(hook); GC.KeepAlive(callback); }
}

public sealed class ClipboardSnapshot : IDisposable
{
    public DataObject Data { get; } = new();
    public bool Empty { get; private set; }
    private readonly List<IDisposable> copies = [];

    public static T Retry<T>(Func<T> operation)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return operation(); }
            catch (ExternalException) when (attempt < 9) { Thread.Sleep(30); }
        }
    }

    public static ClipboardSnapshot Capture()
    {
        var snapshot = new ClipboardSnapshot();
        try
        {
            var original = Retry(Clipboard.GetDataObject);
            string[] formats = original?.GetFormats(false) ?? [];
            snapshot.Empty = formats.Length == 0;
            foreach (string format in formats)
            {
                object? data = Retry(() => original!.GetData(format, format == DataFormats.Bitmap));
                if (format == DataFormats.Bitmap && data is not Image)
                {
                    var bitmap = Retry(ReadNativeBitmap);
                    snapshot.Data.SetData(format, false, bitmap);
                    snapshot.copies.Add(bitmap);
                    continue;
                }
                object copy = data switch
                {
                    string text => text,
                    string[] paths => paths.ToArray(),
                    byte[] bytes => bytes.ToArray(),
                    System.Collections.Specialized.StringCollection paths => CopyPaths(paths),
                    Image image => new Bitmap(image),
                    Stream stream => CopyStream(stream),
                    _ => throw new InvalidOperationException($"Clipboard format '{format}' cannot be safely preserved. Copy ordinary text first; your transcript is in history.")
                };
                snapshot.Data.SetData(format, false, copy);
                if (copy is IDisposable disposable) snapshot.copies.Add(disposable);
            }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    private static Bitmap ReadNativeBitmap()
    {
        if (!Native.OpenClipboard(0)) throw new ExternalException("Clipboard is busy.");
        try
        {
            nint handle = Native.GetClipboardData(2); // CF_BITMAP; some native apps cannot convert through IDataObject.
            if (handle == 0) throw new InvalidOperationException("The clipboard image is unavailable. Your transcript is in history.");
            return Image.FromHbitmap(handle);
        }
        finally { Native.CloseClipboard(); }
    }

    private static System.Collections.Specialized.StringCollection CopyPaths(System.Collections.Specialized.StringCollection paths)
    {
        var copy = new System.Collections.Specialized.StringCollection();
        copy.AddRange(paths.Cast<string>().ToArray());
        return copy;
    }

    private static MemoryStream CopyStream(Stream source)
    {
        if (!source.CanSeek) throw new InvalidOperationException("Clipboard stream cannot be safely preserved.");
        long position = source.Position;
        var copy = new MemoryStream();
        try { source.Position = 0; source.CopyTo(copy); copy.Position = 0; return copy; }
        catch { copy.Dispose(); throw; }
        finally { source.Position = position; }
    }

    public void Restore() => Retry(() => { if (Empty) Clipboard.Clear(); else Clipboard.SetDataObject(Data, true); return true; });
    public void Dispose() { foreach (var copy in copies) copy.Dispose(); }
}

public static class ClipboardPaste
{
    public static async Task PasteAsync(string text, int restoreDelayMs)
    {
        // Do not release physical modifier keys on the user's behalf.
        for (int attempt = 0; ; attempt++)
        {
            bool modifiers = new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => Native.GetAsyncKeyState(key) < 0);
            if (!modifiers) break;
            if (attempt == 100) throw new InvalidOperationException("Release modifier keys before pasting. Copy your transcript from history.");
            await Task.Delay(20);
        }
        uint previousSequence = Native.GetClipboardSequenceNumber();
        using var snapshot = ClipboardSnapshot.Capture();
        if (Native.GetClipboardSequenceNumber() != previousSequence)
            throw new InvalidOperationException("Clipboard changed before paste. Copy your transcript from history.");
        ClipboardSnapshot.Retry(() => { Clipboard.SetText(text); return true; });
        uint sequence = Native.GetClipboardSequenceNumber();
        // shortcut: slow apps may consume paste after this delay, increase clipboardRestoreDelayMs if needed.
        try { Native.Paste(); await Task.Delay(restoreDelayMs); }
        finally
        {
            // A newer user/app copy wins; never overwrite it with our snapshot.
            if (Native.GetClipboardSequenceNumber() == sequence) snapshot.Restore();
        }
    }
}
