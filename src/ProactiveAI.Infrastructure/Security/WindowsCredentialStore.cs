using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ProactiveAI.Core.Abstractions;

namespace ProactiveAI.Infrastructure.Security;

/// <summary>
/// Stores the OpenAI API key as a generic credential in Windows Credential Manager (protected per
/// user by DPAPI). The key never touches settings JSON, logs or exception messages.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    public const string DefaultTarget = "ProactiveAI/OpenAI-API-Key";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private readonly string _target;

    public WindowsCredentialStore(string target = DefaultTarget) => _target = target;

    public bool HasApiKey()
    {
        if (CredRead(_target, CredTypeGeneric, 0, out var ptr))
        {
            CredFree(ptr);
            return true;
        }
        return false;
    }

    public string? ReadApiKey()
    {
        if (!CredRead(_target, CredTypeGeneric, 0, out var ptr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound)
            {
                return null;
            }
            throw new InvalidOperationException($"Credential Manager read failed (error {err}).");
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
            {
                return null;
            }
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var key = Encoding.Unicode.GetString(bytes).Trim();
            Array.Clear(bytes);
            return key;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void WriteApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        apiKey = apiKey.Trim();
        var blob = Encoding.Unicode.GetBytes(apiKey);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = _target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = "openai-api-key",
                Comment = "Proactive AI OpenAI API key",
            };
            if (!CredWrite(ref cred, 0))
            {
                throw new InvalidOperationException($"Credential Manager write failed (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public void DeleteApiKey()
    {
        if (!CredDelete(_target, CredTypeGeneric, 0))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ErrorNotFound)
            {
                throw new Win32Exception(err, "Credential Manager delete failed.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
