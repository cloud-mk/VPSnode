using System;
using System.Threading;
using System.Windows;

namespace VPSnode.Desktop;

public partial class App : Application
{
    static App() => AppContext.SetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);

    private Mutex? _instance;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activationWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (VPSnode.Core.SshPassword.TryAnswer(e.Args) is int exitCode)
        {
            Shutdown(exitCode);
            return;
        }
        base.OnStartup(e);
        var mutexName = @"Local\VPSnodeDesktop";
        var data = Environment.GetEnvironmentVariable("VPSNODE_DATA");
        if (!string.IsNullOrWhiteSpace(data))
        {
            var path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(data));
            var standard = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPSnode");
            if (!path.Equals(standard, StringComparison.OrdinalIgnoreCase))
                mutexName += Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        }
        var activationName = mutexName + ".Activate";
        _instance = new Mutex(true, mutexName, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(activationName, out var activation))
            {
                using (activation) activation.Set();
            }
            else MessageBox.Show("VPSnode 已运行，请从系统托盘打开。", "VPSnode");
            Shutdown();
            return;
        }
        try
        {
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
                Dispatcher.BeginInvoke(new Action(window.ShowWindow)), null, Timeout.Infinite, false);
        }
        catch
        {
            MessageBox.Show("无法打开本地数据，请检查 VPSnode 数据目录。", "VPSnode");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _activate?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
