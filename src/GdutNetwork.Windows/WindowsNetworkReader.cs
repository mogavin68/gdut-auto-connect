using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using GdutNetwork.Core;

namespace GdutNetwork.Windows;

public sealed class WindowsNetworkReader
{
    public NetworkSnapshot Read()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        var adapterInfos = adapters.Where(n => Index(n) > 0).Select(n => new AdapterInfo(n.Name, n.NetworkInterfaceType.ToString(), n.OperationalStatus.ToString(),
            Index(n), n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork),
            n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))).ToArray();
        uint status = Native.WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (status != 0) return new([], adapterInfos, $"WlanOpenHandle:{status}");
        try
        {
            status = Native.WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (status != 0) return new([], adapterInfos, $"WlanEnumInterfaces:{status}");
            try
            {
                int count = Marshal.ReadInt32(list);
                if (count is < 0 or > 64) return new([], adapterInfos, "InvalidInterfaceList");
                var wifi = new List<WifiInfo>();
                int size = Marshal.SizeOf<Native.InterfaceInfo>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<Native.InterfaceInfo>(IntPtr.Add(list, 8 + i * size));
                    var adapter = adapters.FirstOrDefault(a => Guid.TryParse(a.Id, out var id) && id == item.Id);
                    string? ssid = null;
                    int? error = null;
                    if (item.State == 1)
                    {
                        var id = item.Id;
                        uint result = Native.WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out uint bytes, out var attributes, out _);
                        if (result != 0) error = (int)result;
                        else
                        {
                            try
                            {
                                // WLAN_CONNECTION_ATTRIBUTES: state/mode + WCHAR profile[256] + DOT11_SSID.
                                if (bytes < 556) error = -1;
                                else
                                {
                                    int length = Marshal.ReadInt32(attributes, 520);
                                    if (length is < 0 or > 32) error = -1;
                                    else
                                    {
                                        var value = new byte[length];
                                        Marshal.Copy(IntPtr.Add(attributes, 524), value, 0, length);
                                        ssid = Encoding.UTF8.GetString(value);
                                    }
                                }
                            }
                            finally { Native.WlanFreeMemory(attributes); }
                        }
                    }
                    bool? radio = null;
                    var radioId = item.Id;
                    if (Native.WlanQueryInterface(handle, ref radioId, 4, IntPtr.Zero, out uint radioBytes, out var radioData, out _) == 0)
                    {
                        try
                        {
                            int phys = Marshal.ReadInt32(radioData);
                            if (phys is > 0 and <= 64 && radioBytes >= 4 + 12 * phys)
                            {
                                bool anyOn = false, allOff = true;
                                for (int phy = 0; phy < phys; phy++)
                                {
                                    int software = Marshal.ReadInt32(radioData, 8 + phy * 12);
                                    int hardware = Marshal.ReadInt32(radioData, 12 + phy * 12);
                                    anyOn |= software == 1 && hardware == 1;
                                    allOff &= software == 2 || hardware == 2;
                                }
                                radio = anyOn ? true : allOff ? false : null;
                            }
                        }
                        finally { Native.WlanFreeMemory(radioData); }
                    }
                    wifi.Add(new(adapter?.Name ?? "WLAN", adapter is null ? -1 : Index(adapter), StateName(item.State), ssid, error, radio));
                }
                return new(wifi, adapterInfos);
            }
            finally { Native.WlanFreeMemory(list); }
        }
        finally { Native.WlanCloseHandle(handle, IntPtr.Zero); }
    }

    public static RouteObservation GetRoute(IPAddress destination, int? targetIndex, IPAddress? localAddress = null)
    {
        byte[] socketAddress = new byte[destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 16 : 28];
        socketAddress[0] = destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? (byte)2 : (byte)23;
        destination.GetAddressBytes().CopyTo(socketAddress, socketAddress.Length == 16 ? 4 : 8);
        if (socketAddress.Length == 28) BitConverter.GetBytes((uint)destination.ScopeId).CopyTo(socketAddress, 24);
        uint result = Native.GetBestInterfaceEx(socketAddress, out uint index);
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        string? routeName = result == 0 ? adapters.FirstOrDefault(n => Index(n) == index)?.Name : null;
        string? sourceName = localAddress is null ? null : adapters.FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(localAddress)))?.Name;
        bool? matches = result == 0 && targetIndex is > 0 ? index == targetIndex : null;
        return new(routeName, result == 0 ? (int)index : null, matches, sourceName);
    }

    private static int Index(NetworkInterface adapter)
    {
        try { return adapter.GetIPProperties().GetIPv4Properties()?.Index ?? adapter.GetIPProperties().GetIPv6Properties()?.Index ?? -1; }
        catch (NetworkInformationException) { return -1; }
    }

    private static string StateName(int state) => state switch
    {
        0 => "NotReady", 1 => "Connected", 2 => "AdHoc", 3 => "Disconnecting", 4 => "Disconnected",
        5 => "Associating", 6 => "Discovering", 7 => "Authenticating", _ => "Unknown"
    };

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct InterfaceInfo
        {
            public Guid Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            public int State;
        }
        [DllImport("wlanapi.dll")] internal static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
        [DllImport("wlanapi.dll")] internal static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] internal static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] internal static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out uint bytes, out IntPtr data, out int type);
        [DllImport("wlanapi.dll")] internal static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("iphlpapi.dll")] internal static extern uint GetBestInterfaceEx([In] byte[] destination, out uint index);
    }
}
