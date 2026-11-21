# 0.1.10 validation / 验收边界

Base: published v0.1.9, source commit `1ac9123a53cf5c565caf18fbe4e125a6dede6ee0`. Windows 10 LTSC x64, .NET SDK 10.0.404. All fixtures, databases, captures and build outputs use an isolated local task directory. No production asset database or remote VPS is modified.

## Reproduction and cause

On the unmodified v0.1.9 source, a real loopback VLESS/Xray test reproduced three page replacements for each individual/batch latency/download run. A 260 DIP manually sized name column was reset to 160/176 DIP, and row selection was lost. Batch download reproduction invoked the existing runner; the fixed-version integration also clicks the budget dialog.

The actual path is `Monitor.Changed → TaskChanged → Dispatcher → Render → NodesPage → new DataGrid → Loaded/FitAfterLayout`. `Run` also called `Render` at start/finally; `RefreshIfChanged` rebuilt the page on database changes. Independent per-grid sizing and height-triggered refitting compounded the effect. The replacement loses per-control widths, selection and internal scroll state before fitting runs again.

## Automated Windows checks

- Release solution build: zero warnings/errors.
- Existing seven default suites, plus the new StabilityTests and opt-in ProxyUiTests: all passed. No live-host flags used.
- Real local Xray from the existing v2rayN installation: individual and batch latency/download, real origin requests and byte limit; live resizing during probes; real HTTP failure, active cancellation and the real 18-second request timeout. Fixed-version checks continuously retain the same page/grid/row instances, 260 DIP width and selected row, with terminal states visible.
- Three groups and 36 fictional nodes: shared initial/manual widths; progress and final success/failure/timeout/cancel states from worker notifications; selected text, selected row and vertical scroll preservation; unchanged property suppression; download scale/row height stability.
- Window widths 680/1024/1440 DIP, minimum widths, horizontal scrolling, synchronized scroll/column reorder, page return, search and group addition/removal. Old grid notifications are detached.
- Light/dark header separators and retained native resize thumb/cursor; actual WPF visual-tree rasterization at 100% and 150%. Rasterization does not prove physical DPI transitions.
- Actual review/configuration windows: simultaneous left/right, reuse on repeated clicks, real source labels, masked visible credentials, no edit/navigation buttons, pair switching, reopening a closed side and owner-close cleanup.
- Association: unknown versus known data, conflict rejection with transaction unchanged, notes and metadata retention, complete encrypted snapshots, same-timestamp history, task/website/queue remapping, subsequent sync, external fixture v2rayN database hash unchanged, encrypted backup/restore and older backup replay.

## Native input and release checks

Native Windows mouse input confirmed navigation to the fictional node page, dragging a header boundary with matching movement in other groups, and retained width after clicking batch latency. It also confirmed simultaneous left/right configuration windows, real source labels and default credential masking, repeated-click reuse, old-window cleanup on pair switch, independent movement and owner-close cleanup. Automated lifecycle checks above are distinct from these native mouse checks. Packaged-artifact observations and hashes accompany the local delivery report.

Release packages follow the existing self-contained Windows x64 ZIP/MSI flow. The existing icon, MSI upgrade identity and v0.1.9 screenshot files are retained. No GitHub tag, release, asset or branch is changed before the user's publication approval. Local package hashes and verification results accompany the delivery.

## Remaining limits

- No external VPS throughput/performance tests or production-database mutation tests. Real proxy coverage uses isolated loopback VLESS; other protocols retain existing regression coverage.
- Physical Windows display scaling changes at 100/150%, multi-monitor movement and prolonged observation remain unverified; rasterized captures are labeled separately.
- Clean-machine installation/upgrade/uninstallation, cross-computer DPAPI recovery and long-term soak testing remain unverified. MSI administrative extraction is not installation/upgrade acceptance.
- A source whose known route conflicts with a previously confirmed association now becomes a separate candidate. This deliberately avoids silently reusing an old confirmation for incompatible configuration.
- Existing metadata values remain active on association; conflicting metadata and both configurations remain accessible in encrypted pre-association snapshots. Previously lost data from older versions cannot be recovered by this update.
