using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace BeckhoffMcp.AdsBridge.Native;

/// <summary>
/// Hosts Beckhoff's native TcAmsServer.dll in our process. After startup, the
/// DLL registers a Win32 window class (TcAmsWindow) that TcAdsDll-based
/// clients (pyads / .NET AdsClient) automatically discover and connect to.
/// Effectively makes our process look like a TwinCAT System Service to clients.
/// </summary>
public sealed class TcAmsServerHost : IDisposable
{
    private const string Dll = "TcAmsServer";
    private readonly ILogger<TcAmsServerHost> _log;
    private bool _started;
    private Thread? _msgLoop;
    private CancellationTokenSource? _stopCts;

    public TcAmsServerHost(ILogger<TcAmsServerHost> log) => _log = log;

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int AmsServerAPIStartup();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int AmsServerAPICleanup();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetServerAddress(out long amsAddrPtr);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out NativeMessage msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref NativeMessage msg);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    public void Start()
    {
        if (_started) return;

        _log.LogInformation("Calling AmsServerAPIStartup() ...");
        int rc = AmsServerAPIStartup();
        _log.LogInformation("AmsServerAPIStartup returned {Rc}", rc);
        if (rc != 0) throw new InvalidOperationException($"AmsServerAPIStartup failed with code {rc}");
        _started = true;

        // Probe for the registered window class so we know clients can find us.
        for (var i = 0; i < 20; i++)
        {
            var hwnd = FindWindowW("TcAmsWindow", null);
            if (hwnd != IntPtr.Zero)
            {
                _log.LogInformation("TcAmsWindow registered: hwnd=0x{Hwnd:X}", hwnd.ToInt64());
                break;
            }
            Thread.Sleep(50);
        }

        _stopCts = new CancellationTokenSource();
        _msgLoop = new Thread(MessageLoop) { IsBackground = false, Name = "TcAmsServer-MsgLoop" };
        _msgLoop.SetApartmentState(ApartmentState.STA);
        _msgLoop.Start();
    }

    private void MessageLoop()
    {
        _log.LogDebug("Win32 message loop started");
        while (_stopCts?.IsCancellationRequested == false)
        {
            int r = GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0) break; // WM_QUIT
            if (r == -1) { _log.LogWarning("GetMessage error"); break; }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        _log.LogDebug("Win32 message loop ended");
    }

    public void Dispose()
    {
        _stopCts?.Cancel();
        if (_started)
        {
            try { AmsServerAPICleanup(); } catch (Exception ex) { _log.LogWarning(ex, "AmsServerAPICleanup failed"); }
            _started = false;
        }
        _stopCts?.Dispose();
    }
}
