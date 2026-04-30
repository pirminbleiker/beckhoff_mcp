using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BeckhoffMcp.Server.Services;

/// <summary>
/// Talks to the Windows Credential Manager via credui.dll/advapi32.dll. Same
/// surface as the credential prompt RDP / SMB use: target name keyed by
/// "BeckhoffMcp:&lt;ip-or-host&gt;", optional "Save" checkbox writes the entry to
/// the per-user vault (DPAPI-encrypted), no plaintext ever touches our config
/// file or the agent transcript.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialPrompt
{
    private readonly ILogger<WindowsCredentialPrompt> _log;
    public WindowsCredentialPrompt(ILoggerFactory lf) => _log = lf.CreateLogger<WindowsCredentialPrompt>();

    public static string TargetName(string ipOrHost) => $"BeckhoffMcp:{ipOrHost}";

    public bool TryRead(string ipOrHost, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;
        var target = TargetName(ipOrHost);
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var credPtr))
            return false;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            username = cred.UserName ?? string.Empty;
            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
            {
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                // The blob is UTF-16LE without trailing NUL — that's how we wrote it.
                password = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                Array.Clear(bytes);
            }
            return true;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public void Save(string ipOrHost, string username, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetName(ipOrHost),
                UserName = username,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWriteW(ref cred, 0))
                throw new InvalidOperationException(
                    $"CredWriteW failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocAnsi(blobPtr); // works for any ptr; clears bytes
            Array.Clear(blob);
        }
    }

    public bool Delete(string ipOrHost) =>
        CredDeleteW(TargetName(ipOrHost), CRED_TYPE_GENERIC, 0);

    /// <summary>
    /// Opens the standard Windows credential dialog (the same one RDP and SMB
    /// use). Returns true if the user submitted; password is the plaintext we
    /// pass straight into the AddRoute UDP packet. The caller is expected to
    /// zero it after use.
    /// </summary>
    public bool Prompt(string ipOrHost, string message, string? defaultUsername,
        out string username, out string password, out bool saveRequested)
    {
        username = string.Empty;
        password = string.Empty;
        saveRequested = false;

        var info = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            hwndParent = IntPtr.Zero,
            pszMessageText = message,
            pszCaptionText = $"Beckhoff MCP — Add ADS route on {ipOrHost}",
            hbmBanner = IntPtr.Zero,
        };

        // Pre-fill the dialog with the caller-supplied username (if any).
        IntPtr inAuthBuf = IntPtr.Zero;
        uint inAuthSize = 0;
        if (!string.IsNullOrEmpty(defaultUsername))
            CredPackAuthenticationBufferW(0x00, defaultUsername, "",
                IntPtr.Zero, ref inAuthSize);
        if (inAuthSize > 0)
        {
            inAuthBuf = Marshal.AllocHGlobal((int)inAuthSize);
            CredPackAuthenticationBufferW(0x00, defaultUsername, "",
                inAuthBuf, ref inAuthSize);
        }

        IntPtr outAuthBuf = IntPtr.Zero;
        uint outAuthSize = 0;
        int authPackage = 0;
        bool save = false;

        try
        {
            var rc = CredUIPromptForWindowsCredentialsW(
                ref info,
                0,
                ref authPackage,
                inAuthBuf, inAuthSize,
                out outAuthBuf, out outAuthSize,
                ref save,
                CREDUIWIN_GENERIC | CREDUIWIN_CHECKBOX);
            if (rc != 0)
            {
                _log.LogDebug("Credential dialog cancelled or failed (rc={Rc})", rc);
                return false;
            }
            saveRequested = save;

            // Unpack the buffer → username + password (plaintext UTF-16).
            var userBuf = new StringBuilder(513);
            uint userLen = (uint)userBuf.Capacity;
            var domainBuf = new StringBuilder(257);
            uint domainLen = (uint)domainBuf.Capacity;
            var passBuf = new StringBuilder(257);
            uint passLen = (uint)passBuf.Capacity;

            if (!CredUnPackAuthenticationBufferW(0x01,
                    outAuthBuf, outAuthSize,
                    userBuf, ref userLen,
                    domainBuf, ref domainLen,
                    passBuf, ref passLen))
            {
                _log.LogWarning("CredUnPackAuthenticationBufferW failed: {Err}",
                    Marshal.GetLastWin32Error());
                return false;
            }

            var rawUser = userBuf.ToString().TrimEnd('\0');
            var domain = domainBuf.ToString().TrimEnd('\0');
            username = string.IsNullOrEmpty(domain) ? rawUser : $"{domain}\\{rawUser}";
            password = passBuf.ToString().TrimEnd('\0');
            // Clear the StringBuilder backing storage so the password isn't
            // sitting around in our heap longer than necessary.
            passBuf.Clear();
            return true;
        }
        finally
        {
            if (inAuthBuf != IntPtr.Zero)
            {
                ZeroMemory(inAuthBuf, (int)inAuthSize);
                Marshal.FreeHGlobal(inAuthBuf);
            }
            if (outAuthBuf != IntPtr.Zero)
            {
                ZeroMemory(outAuthBuf, (int)outAuthSize);
                CoTaskMemFree(outAuthBuf);
            }
        }
    }

    // --- Win32 -----------------------------------------------------------

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int CREDUIWIN_GENERIC  = 0x1;
    private const int CREDUIWIN_CHECKBOX = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
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
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(string target, int type, int flags);
    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr cred);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredUIPromptForWindowsCredentialsW(
        ref CREDUI_INFO notUsedHere,
        int authError,
        ref int authPackage,
        IntPtr InAuthBuffer,
        uint InAuthBufferSize,
        out IntPtr refOutAuthBuffer,
        out uint refOutAuthBufferSize,
        ref bool fSave,
        int flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredPackAuthenticationBufferW(
        int dwFlags, string pszUserName, string pszPassword,
        IntPtr pPackedCredentials, ref uint pcbPackedCredentials);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBufferW(
        int dwFlags,
        IntPtr pAuthBuffer, uint cbAuthBuffer,
        StringBuilder pszUserName, ref uint pcchMaxUserName,
        StringBuilder pszDomainName, ref uint pcchMaxDomainName,
        StringBuilder pszPassword, ref uint pcchMaxPassword);

    [DllImport("kernel32.dll")]
    private static extern void RtlZeroMemory(IntPtr destination, int length);
    private static void ZeroMemory(IntPtr p, int len) => RtlZeroMemory(p, len);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);
}
