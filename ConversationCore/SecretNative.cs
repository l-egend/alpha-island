using System.Runtime.InteropServices;
namespace alpha_island.Services;
internal static class SecretNative {
 [StructLayout(LayoutKind.Sequential)] internal struct DATA_BLOB { public int cbData; public IntPtr pbData; }
 [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CryptProtectData(ref DATA_BLOB data,string description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out DATA_BLOB result);
 [DllImport("crypt32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CryptUnprotectData(ref DATA_BLOB data,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out DATA_BLOB result);
 [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr pointer);
}
