using System.Diagnostics;
using System.Text;

namespace VPSnode.Core;

internal static class ProcessRun
{
    internal static Process Start(string file, IReadOnlyList<string> arguments, string? directory = null, bool input = false, string passwordCipher = "")
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardError = true, RedirectStandardOutput = true,
            RedirectStandardInput = input, StandardInputEncoding = input ? new UTF8Encoding(false) : null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true, UseShellExecute = false
        };
        if (passwordCipher.Length > 0)
        {
            var executable = Environment.ProcessPath ?? throw new IOException("无法定位 SSH 密码助手。");
            var helper = Path.Combine(Path.GetDirectoryName(executable)!, "vpsnode-cli.exe");
            if (!File.Exists(helper) && Path.GetFileName(executable).Equals("VPSnode.exe", StringComparison.OrdinalIgnoreCase))
                helper = executable;
            if (!File.Exists(helper)) helper = Path.Combine(AppContext.BaseDirectory, "vpsnode-cli.exe");
            if (!File.Exists(helper)) throw new IOException("缺少随包 SSH 密码助手。");
            start.Environment["SSH_ASKPASS"] = helper;
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            start.Environment["DISPLAY"] = "VPSnode";
            start.Environment["VPSNODE_ASKPASS"] = passwordCipher;
        }
        if (directory != null) start.WorkingDirectory = directory;
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("无法启动所需程序。");
    }

    internal static async Task<(int Exit, string Output, string Error)> Capture(string file, IReadOnlyList<string> args,
        CancellationToken token, int seconds = 18, string passwordCipher = "")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var process = Start(file, args, passwordCipher: passwordCipher);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("操作超时。"); }
        finally { Stop(process); }
    }

    internal static void Stop(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); process.WaitForExit(2000); }
        catch (InvalidOperationException) { }
    }
}
