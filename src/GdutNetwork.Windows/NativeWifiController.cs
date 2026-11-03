using System.Runtime.InteropServices;
using System.Text;

namespace GdutNetwork.Windows;

public sealed class NativeWifiController : IDisposable
{
    private IntPtr handle;
    private readonly NotificationCallback callback;
    public event Action<bool, bool>? ConnectionChanged;
    public NativeWifiController()
    {
        callback = OnNotification;
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) { handle = IntPtr.Zero; return; }
        WlanRegisterNotification(handle, 8, true, callback, IntPtr.Zero, IntPtr.Zero, out _);
    }

    public async Task<bool> ConnectAsync(CancellationToken token)
    {
        if (handle == IntPtr.Zero) return false;
        var snapshot = new WindowsNetworkReader().Read();
        if (snapshot.Target is not null) return true;
        if (snapshot.Wifi.Count != 1 || snapshot.Wifi[0].RadioOn == false) return false;
        if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return false;
        uint result;
        try
        {
            if (Marshal.ReadInt32(list) != 1) return false;
            Guid id = Marshal.PtrToStructure<Guid>(IntPtr.Add(list, 8));
            var parameters = new ConnectionParameters { Mode = 0, Profile = "gdut", BssType = 1 };
            token.ThrowIfCancellationRequested();
            result = WlanConnect(handle, ref id, ref parameters, IntPtr.Zero);
        }
        finally { WlanFreeMemory(list); }
        if (result != 0) return false;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500, token).ConfigureAwait(false);
            var state = new WindowsNetworkReader().Read();
            if (state.Wifi.Any(w => w.RadioOn == false)) return false;
            if (state.Target is { } target && state.Adapters.Any(a => a.InterfaceIndex == target.InterfaceIndex && (a.HasIpv4 || a.HasIpv6))) return true;
        }
        return false;
    }

    private void OnNotification(ref Notification data, IntPtr context)
    {
        try
        {
            if (data.Source != 8 || data.Code is not (6 or 21) || data.Data == IntPtr.Zero || data.Size < 564) return;
            int length = Marshal.ReadInt32(data.Data, 516);
            if (length is < 1 or > 32) return;
            byte[] bytes = new byte[length]; Marshal.Copy(IntPtr.Add(data.Data, 520), bytes, 0, length);
            if (Encoding.UTF8.GetString(bytes) != "gdut") return;
            bool knownManual = false;
            if (data.Code == 21)
            {
                uint reason = (uint)Marshal.ReadInt32(data.Data, 560);
                var text = new StringBuilder(512);
                if (WlanReasonCodeToString(reason, 512, text, IntPtr.Zero) == 0)
                    knownManual = System.Text.RegularExpressions.Regex.IsMatch(text.ToString(), "用户.*断开|由用户断开|disconnected by.*user|user.*disconnect", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            ConnectionChanged?.Invoke(knownManual, data.Code == 21);
        }
        catch { /* Native callback must not unwind into WLAN AutoConfig. The periodic check remains active. */ }
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        WlanCloseHandle(handle, IntPtr.Zero); handle = IntPtr.Zero;
        GC.KeepAlive(callback);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ConnectionParameters
    {
        public int Mode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Ssid, BssidList;
        public int BssType;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Notification
    {
        public uint Source, Code;
        public Guid InterfaceId;
        public uint Size;
        public IntPtr Data;
    }
    private delegate void NotificationCallback(ref Notification notification, IntPtr context);
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr value);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanConnect(IntPtr handle, ref Guid id, ref ConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(IntPtr handle, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate, NotificationCallback callback, IntPtr context, IntPtr reserved, out uint previous);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanReasonCodeToString(uint reason, uint size, StringBuilder buffer, IntPtr reserved);
}
