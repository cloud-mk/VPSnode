# Development and testing / 开发与测试

Windows x64, .NET 10 SDK, PowerShell 7. Build with `dotnet build VPSnode.slnx -c Release`.

Use a fresh isolated directory for each run. Never point tests at your normal application data directory.
每次测试使用全新隔离目录，不得使用日常台账目录。

```powershell
$suites = 'Smoke', 'BackupTests', 'IdentityTests', 'InventoryChecks', 'UiBatch', 'FormTests', 'UiRegression', 'StabilityTests', 'AgentTests'
foreach ($suite in $suites) {
    dotnet run --project "tests/VPSnode.$suite/VPSnode.$suite.csproj" -c Release -- "../test-data/$suite"
    if ($LASTEXITCODE) { throw "Failed: $suite" }
}
```

Default suites use synthetic fixtures. WPF checks run application event handlers in-process; they do not prove OS mouse, keyboard, or file-picker behavior.
默认测试使用虚构数据。WPF 测试在进程内执行界面事件，不等同于系统鼠标、键盘或文件选择器验收。

0.1.11 的 AgentTests 真正启动独立 CLI/worker 子进程，覆盖资产、设置、来源、任务、失败退出码、加密恢复及回环 HTTP。ProxyUiTests 增加 CLI/GUI 跨进程进度、取消、等待超时、真实执行进程被终止后的恢复及过期 TLS 证书握手；仍需显式指定本机已有的 Xray，不能用假检测替代。详见 [Agent CLI](agent-cli.md)、[覆盖矩阵](coverage-0.1.11.md) 与 [验收边界](validation-0.1.11.md)。

Optional live tests require explicit authorization for the target host. For IdentityTests `--real`, set `VPSNODE_TEST_SSH_ALIAS` and `VPSNODE_TEST_V2RAYN_DIRECTORY`. FormTests accepts `--live-ui SSH_ALIAS`. These modes read your own configuration and contact the specified host. Do not publish their output or run them in public CI.
真实环境测试仅适用于已授权主机，使用上述环境变量或参数指定本地配置。不要公开其输出，也不要在公开 CI 中执行。

Architecture: `VPSnode.Core` handles storage, inventory and probes; `VPSnode.Desktop` contains the WPF interface; `VPSnode.Cli` provides local commands. Keep configured state, listener evidence, and successful proxy requests separate. Never merge different client identities or infer fallbacks from a share address alone.

Public contributions must use fictional identifiers and documentation IP ranges. Do not commit databases, backups, credentials, personal paths, or screenshots of real assets. Release packages must include both READMEs, LICENSE, NOTICE, and dependency notices.

For the opt-in loopback proxy suite, screenshot rendering and the exact verification limits of this version, see [validation-0.1.9.md](validation-0.1.9.md). The portable package also includes these documents, screenshots and CHANGELOG.md.

0.1.10 adds `StabilityTests` for multiple groups, in-place progress, selection/scroll preservation, column width/reorder/scroll sharing, theme rendering, retired-grid event cleanup and real comparison window lifecycle. These checks run WPF controls and Dispatcher callbacks, not OS input. `ProxyUiTests` additionally exercises all four test buttons through an existing Xray on loopback and checks control identity and manual widths throughout. See [validation-0.1.10.md](validation-0.1.10.md).

Node association retains the left local ID and its existing configuration, fills missing fields, merges notes/provenance/history and remaps website/task/queue references. Both original configurations are retained as encrypted `NodeSource.Configuration` snapshots (optional JSON field, no SQLite schema change). Known conflicts block manual association. A later incompatible source route is retained as a separate review record, including associations confirmed by an older version. Association does not write to SSH or v2rayN. Existing 0.1.9 data and backups remain readable; information already discarded by an older merge cannot be reconstructed retroactively.
