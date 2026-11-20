using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace VPSnode.Desktop;

internal sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8000 + 41;
    private const uint MessageFlag = 1, IconFlag = 2, TipFlag = 4, InfoFlag = 16, GuidFlag = 32, ShowTipFlag = 128;
    private const uint Add = 0, Modify = 1, Delete = 2, SetVersion = 4;
    private readonly HwndSource _source;
    private readonly Guid _id;
    private readonly Action _open;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr _handle;
    private bool _disposed;

    internal TrayIcon(HwndSource source, string directory, Action open, Action quit)
    {
        _source = source;
        _handle = source.Handle;
        _open = open;
        // Explorer associates a GUID with an executable path. Portable copies and separate data profiles have distinct identities.
        var identity = Environment.ProcessPath + "|" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())).AsSpan(0, 16));
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("打开 VPSnode", null, (_, _) => _open());
        _menu.Items.Add("退出", null, (_, _) => quit());
        _source.AddHook(Messages);
        // An abruptly terminated process cannot remove its previous icon.
        Notify(Delete, Data());
        Register();
        AppDomain.CurrentDomain.ProcessExit += ProcessExit;
    }

    private IconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<IconData>(), Window = _handle, Id = 1, Flags = GuidFlag, Identity = _id,
        Tip = "", Info = "", Title = ""
    };

    private void Register()
    {
        var icon = Data();
        icon.Flags |= MessageFlag | IconFlag | TipFlag | ShowTipFlag;
        icon.Callback = Callback;
        icon.Icon = System.Drawing.SystemIcons.Application.Handle;
        icon.Tip = "VPSnode · 本机巡检";
        if (!Notify(Add, icon) && !Notify(Modify, icon)) return;
        icon.Version = 4;
        Notify(SetVersion, icon);
    }

    private IntPtr Messages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if ((uint)message == _taskbarCreated) Register();
        if ((uint)message == Callback)
        {
            switch ((int)(lParam.ToInt64() & 0xffff))
            {
                case 0x0203: // WM_LBUTTONDBLCLK
                case 0x0401: // NIN_KEYSELECT
                    _open();
                    break;
                case 0x007b: // WM_CONTEXTMENU with NOTIFYICON_VERSION_4
                    SetForegroundWindow(_handle);
                    var point = new System.Drawing.Point(unchecked((short)wParam.ToInt64()),
                        unchecked((short)(wParam.ToInt64() >> 16)));
                    _menu.Show(point.X == -1 && point.Y == -1 ? Forms.Cursor.Position : point);
                    break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    internal void Notice(string title, string text)
    {
        if (_disposed) return;
        var data = Data();
        data.Flags |= InfoFlag;
        data.InfoFlags = 2; // NIIF_WARNING
        data.Title = title[..Math.Min(title.Length, 63)];
        data.Info = text[..Math.Min(text.Length, 255)];
        Notify(Modify, data);
    }

    private void ProcessExit(object? sender, EventArgs e) => Notify(Delete, Data());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= ProcessExit;
        Notify(Delete, Data());
        _source.RemoveHook(Messages);
        _menu.Dispose();
    }

    private static bool Notify(uint action, IconData data) => Shell_NotifyIcon(action, ref data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id, Flags, Callback;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Title;
        internal uint InfoFlags;
        internal Guid Identity;
        internal IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint action, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
