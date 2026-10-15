using System.Runtime.InteropServices;

namespace Holdtalk;

public static class WindowsMicrophone
{
    public static string DefaultName()
    {
        object enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        Device? device = null;
        PropertyStore? properties = null;
        var value = new PropertyValue();
        try
        {
            ((DeviceEnumerator)enumerator).GetDefaultAudioEndpoint(1, 1, out device); // Capture, multimedia role.
            device.OpenPropertyStore(0, out properties);
            var key = new PropertyKey { Format = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            properties.GetValue(ref key, out value);
            if (value.Type != 31) throw new InvalidOperationException("Windows microphone friendly name is unavailable.");
            return Marshal.PtrToStringUni(value.Text) ?? "";
        }
        finally
        {
            PropVariantClear(ref value);
            if (properties is not null) Marshal.ReleaseComObject(properties);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Text; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint state, out nint devices);
        void GetDefaultAudioEndpoint(int flow, int role, out Device device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Device
    {
        void Activate(ref Guid id, uint context, nint parameters, out nint instance);
        void OpenPropertyStore(uint access, out PropertyStore properties);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface PropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropertyValue value);
    }
}
