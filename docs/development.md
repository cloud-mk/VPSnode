# Development and testing / 开发与测试

Windows x64, .NET 10 SDK, PowerShell 7. Build with `dotnet build VPSnode.slnx -c Release`.

Use a fresh isolated directory for each run. Never point tests at your normal application data directory.
每次测试使用全新隔离目录，不得使用日常台账目录。

```powershell
$suites = 'Smoke', 'BackupTests', 'IdentityTests', 'InventoryChecks', 'UiBatch', 'FormTests'
foreach ($suite in $suites) {
    dotnet run --project "tests/VPSnode.$suite/VPSnode.$suite.csproj" -c Release -- "../test-data/$suite"
    if ($LASTEXITCODE) { throw "Failed: $suite" }
}
```

Default suites use synthetic fixtures. WPF checks run application event handlers in-process; they do not prove OS mouse, keyboard, or file-picker behavior.
默认测试使用虚构数据。WPF 测试在进程内执行界面事件，不等同于系统鼠标、键盘或文件选择器验收。

Optional live tests require explicit authorization for the target host. For IdentityTests `--real`, set `VPSNODE_TEST_SSH_ALIAS` and `VPSNODE_TEST_V2RAYN_DIRECTORY`. FormTests accepts `--live-ui SSH_ALIAS`. These modes read your own configuration and contact the specified host. Do not publish their output or run them in public CI.
真实环境测试仅适用于已授权主机，使用上述环境变量或参数指定本地配置。不要公开其输出，也不要在公开 CI 中执行。

Architecture: `VPSnode.Core` handles storage, inventory and probes; `VPSnode.Desktop` contains the WPF interface; `VPSnode.Cli` provides local commands. Keep configured state, listener evidence, and successful proxy requests separate. Never merge different client identities or infer fallbacks from a share address alone.

Public contributions must use fictional identifiers and documentation IP ranges. Do not commit databases, backups, credentials, personal paths, or screenshots of real assets. Release packages must include both READMEs, LICENSE, NOTICE, and dependency notices.
