using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Kumo.Services;

public static class DpapiProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(Transform(bytes, protect: true));
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), protect: false));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPtr = Marshal.AllocHGlobal(input.Length);
        var output = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inputPtr, input.Length);
            var inBlob = new DataBlob { Size = input.Length, Data = inputPtr };
            var ok = protect
                ? CryptProtectData(ref inBlob, "Kumo token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            Marshal.FreeHGlobal(inputPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
