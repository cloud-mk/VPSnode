# 0.1.9 validation / 验收记录

Environment: Windows 10 x64 build 19044 (LTSC), .NET SDK 10.0.404. All checks use isolated fictional assets. No production servers were contacted for this release's verification.

## Passed / 已验证

- Release solution build with warnings treated as errors.
- Smoke, BackupTests, IdentityTests, InventoryChecks, UiBatch, FormTests and UiRegression. Existing suites cover storage, imports, forms, QR, task cancellation and backup integrity. Added assertions cover legacy/default scheduler state, persistent on/off choice, interval, manual and CLI queued tasks while off, decimal MB precision, theme draft preservation, password resource bindings, recycled text selection and node minimum widths.
- Encrypted backup restore preserves an explicitly enabled scheduler setting. Missing settings properties deserialize to disabled; no database rewrite is needed.
- Opt-in ProxyUiTests used an existing Xray 26.6.1 binary from v2rayN, with a temporary VLESS server and HTTP origin on loopback. Invoked the actual list button handlers: two proxy latency requests, one download, a 256,000-byte cap, disabled duplicate actions and two recorded task histories. This validates a real local proxy path, not Internet/VPS performance or OS mouse input.
- Actual running WPF pages rendered in light/dark themes at 680, 1024 and 1280 DIP window widths, with 100%, 150% and 200% rasterization scales. These are rendering checks, not physical display DPI transitions. Published screenshots contain fictional data only.
- Shared PasswordBox style uses dynamic foreground/background/caret resources; SSH and backup forms retain their existing validation and password handling.
- Multi-size icon includes 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px, with transparent edges. SVG source is retained. WPF owns DPI startup; the WinForms-only WFO0003 analyzer is suppressed specifically because WinForms is used for dialogs and menus, not application startup.
- Self-contained portable CLI reports 0.1.9 and imports fictional assets. Windows Installer administrative extraction succeeds and its payload hashes match the portable files; this is not a clean installation test. MSI ProductVersion, preserved UpgradeCode, application icon and Start menu shortcut icon references were inspected. The executable's extracted 32 px icon matches the approved ICO pixel for pixel.
- The packaged desktop app registers its tray icon, recovers registration after forced termination, and removes it on three subsequent clean exits. This automated lifecycle check uses Windows UI Automation for the Exit action; it does not verify the icon's visual appearance or real mouse input.

## Not verified / 未验证

- OS-level single click, drag selection, clipboard, context menu and double-click acceptance. Desktop automation returned expired-session errors; in-process routed events and control tests do not replace these checks.
- Actual Explorer taskbar/tray appearance, shell icon cache behavior after upgrades, physical multi-monitor DPI switching, and clean-system install/upgrade/uninstall.
- Cross-computer backup recovery, external VPS network performance and long-term operation.

## Reproduce / 复验

See [development.md](development.md). For the optional local proxy suite, set `VPSNODE_TEST_V2RAYN_DIRECTORY` to your existing v2rayN installation, then run `dotnet run --project tests/VPSnode.ProxyUiTests -c Release -- <fresh-directory>`. It does not read or modify user node configurations. The temporary server allows only its test HTTP address and port through Xray's default private-address restriction.

Run `VPSnode.UiBatch <fresh-directory> --screenshots` to render the actual application with fictional fixtures. Do not substitute production data for public screenshots.
