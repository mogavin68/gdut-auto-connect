using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using GdutNetwork.Core;

namespace GdutNetwork.Windows;

public sealed class CredentialStore
{
    private readonly string target;
    public CredentialStore(Guid? selfTestId = null) => target = selfTestId is null ? "GDUTAutoNetwork/UniversityTown/v1" : $"GDUTAutoNetwork/SelfTest/{selfTestId:D}";
    public bool Exists => ReadMetadata() is not null;
    public string? ReadMetadata()
    {
        if (!ReadNative(out var pointer)) return null;
        try { return Marshal.PtrToStructure<NativeCredential>(pointer).UserName; }
        finally { CredFree(pointer); }
    }

    public LoginCredential? Read()
    {
        if (!ReadNative(out var pointer)) return null;
        try
        {
            var value = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (value.BlobSize > 2560 || value.BlobSize % 2 != 0) throw new InvalidOperationException("Invalid credential size");
            return new(value.UserName ?? "", Marshal.PtrToStringUni(value.Blob, (int)value.BlobSize / 2) ?? "");
        }
        finally { CredFree(pointer); }
    }

    public void Save(string userName, string password)
    {
        userName = userName.Trim();
        if (userName.Length is < 1 or > 256 || password.Length is < 1 or > 1280 || userName.Contains('\0') || password.Contains('\0'))
            throw new ArgumentException("账号或密码长度无效");
        byte[] bytes = Encoding.Unicode.GetBytes(password);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var value = new NativeCredential
            {
                Type = 1, TargetName = target, Comment = "广工自动联网小工具", BlobSize = (uint)bytes.Length,
                Blob = pointer, Persist = 2, UserName = userName
            };
            if (!CredWriteW(ref value, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(bytes);
            for (int i = 0; i < password.Length * 2; i++) Marshal.WriteByte(pointer, i, 0);
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Delete()
    {
        if (!CredDeleteW(target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private bool ReadNative(out IntPtr pointer)
    {
        if (CredReadW(target, 1, 0, out pointer)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error == 1168) return false;
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWriteW(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
}
