<#
.SYNOPSIS
  Stores (or removes) the OpenAI API key in Windows Credential Manager for Proactive AI.
.DESCRIPTION
  The key is read with a masked prompt and written as a generic credential
  "ProactiveAI/OpenAI-API-Key" (DPAPI-protected per user). It is never printed,
  written to a file, or placed in command history. The app's Settings page offers
  the same replace/delete controls.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Set-ApiKey.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Set-ApiKey.ps1 -Remove
#>
param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$target = 'ProactiveAI/OpenAI-API-Key'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PaiCred {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct CREDENTIAL {
    public uint Flags; public uint Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist;
    public uint AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
  }
  [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern bool CredWrite(ref CREDENTIAL c, uint flags);
  [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern bool CredDelete(string target, int type, int flags);
  public static void Write(string target, IntPtr bstr, int chars) {
    int bytes = chars * 2;
    var c = new CREDENTIAL { Type = 1, TargetName = target, CredentialBlobSize = (uint)bytes, CredentialBlob = bstr,
      Persist = 2, UserName = "openai-api-key", Comment = "Proactive AI OpenAI API key" };
    if (!CredWrite(ref c, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  }
}
'@

if ($Remove) {
  if ([PaiCred]::CredDelete($target, 1, 0)) { Write-Host 'API key removed.' } else { Write-Host 'No stored API key found.' }
  return
}

$secure = Read-Host -AsSecureString -Prompt 'Paste your OpenAI API key (input is hidden)'
if ($secure.Length -lt 20) { throw 'That does not look like an API key; nothing was stored.' }
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
  [PaiCred]::Write($target, $bstr, $secure.Length)
  Write-Host "API key stored in Windows Credential Manager as '$target'."
} finally {
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
  $secure.Dispose()
}
