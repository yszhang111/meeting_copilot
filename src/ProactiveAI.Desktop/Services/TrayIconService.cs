using ProactiveAI.Desktop.Interop;

namespace ProactiveAI.Desktop.Services;

public sealed record TrayMenuItem(int Id, string Text, bool Enabled = true);

/// <summary>
/// Notification-area icon using Shell_NotifyIcon with the main window as the callback target.
/// The window is subclassed to receive icon messages and suspend notifications.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = Win32.WM_APP + 1;
    private readonly IntPtr _hwnd;
    private readonly Win32.SubclassProc _proc;
    private readonly Dictionary<string, IntPtr> _icons = new();
    private IntPtr _suspendRegistration;
    private bool _added;
    private bool _disposed;

    public TrayIconService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        Win32.SetWindowSubclass(hwnd, _proc, (UIntPtr)0x5041, UIntPtr.Zero);
        _suspendRegistration = Win32.RegisterSuspendResumeNotification(hwnd, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    /// <summary>Left click / double click on the icon.</summary>
    public event Action? Activated;

    /// <summary>Raised with the selected menu id.</summary>
    public event Action<int>? MenuItemSelected;

    /// <summary>System is about to suspend (sleep/hibernate/modern standby).</summary>
    public event Action? Suspending;

    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    public void Update(string tooltip, uint argb)
    {
        if (_disposed)
        {
            return;
        }
        string key = argb.ToString("X8");
        if (!_icons.TryGetValue(key, out var icon))
        {
            icon = TrayIconFactory.Create(argb, Math.Max(16, Win32.GetSystemMetrics(49 /* SM_CXSMICON */)) * 2 > 64 ? 32 : 32);
            _icons[key] = icon;
        }
        var data = new Win32.NOTIFYICONDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = icon,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            uVersion = Win32.NOTIFYICON_VERSION_4,
        };
        if (!_added)
        {
            _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref data);
        }
        else
        {
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_added)
        {
            var data = new Win32.NOTIFYICONDATA
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
            };
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            _added = false;
        }
        if (_suspendRegistration != IntPtr.Zero)
        {
            Win32.UnregisterSuspendResumeNotification(_suspendRegistration);
            _suspendRegistration = IntPtr.Zero;
        }
        Win32.RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)0x5041);
        foreach (var icon in _icons.Values)
        {
            Win32.DestroyIcon(icon);
        }
        _icons.Clear();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == CallbackMessage)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev is Win32.WM_LBUTTONUP or Win32.WM_LBUTTONDBLCLK)
            {
                Activated?.Invoke();
            }
            else if (ev is Win32.WM_RBUTTONUP or Win32.WM_CONTEXTMENU)
            {
                ShowMenu();
            }
            return IntPtr.Zero;
        }
        if (msg == Win32.WM_POWERBROADCAST && wParam.ToInt64() == Win32.PBT_APMSUSPEND)
        {
            Suspending?.Invoke();
        }
        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke() ?? [];
        IntPtr menu = Win32.CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.Id < 0)
                {
                    Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
                }
                else
                {
                    Win32.AppendMenu(menu, Win32.MF_STRING | (item.Enabled ? 0 : Win32.MF_GRAYED), (UIntPtr)item.Id, item.Text);
                }
            }
            Win32.GetCursorPos(out var pt);
            // Required so the menu closes when the user clicks elsewhere (documented TrackPopupMenu behavior).
            Win32.SetForegroundWindow(_hwnd);
            uint cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            Win32.PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0)
            {
                MenuItemSelected?.Invoke((int)cmd);
            }
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }
}
