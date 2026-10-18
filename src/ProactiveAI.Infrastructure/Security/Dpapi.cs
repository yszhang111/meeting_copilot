using System.Runtime.InteropServices;

namespace ProactiveAI.Infrastructure.Security;

/// <summary>Windows Data Protection API (CurrentUser scope): only the same Windows account can decrypt.</summary>
internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: true);

    public static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: false);

    private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
    {
        var input = Pin(data, out var inputHandle);
        var extra = Pin(entropy, out var entropyHandle);
        var output = default(DataBlob);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
            if (!ok)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    private static DataBlob Pin(byte[] bytes, out GCHandle handle)
    {
        handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return new DataBlob { Size = bytes.Length, Data = handle.AddrOfPinnedObject() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
