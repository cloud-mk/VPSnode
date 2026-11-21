# Changelog

## 0.1.11 — 2026-11-21

- Complete the local Agent workflow across assets, SSH metadata, sources, duplicate associations, configuration, settings, backups and inspection history, reusing Core validation and encrypted storage.
- Persist task IDs, queued/running/completed/failed/canceled states, progress, item results and error reasons. Run a detached short-lived local worker when the GUI is absent; use one cross-process execution lock and durable cancellation for both entry points.
- Avoid inherited stdout handles delaying Agent submission receipts. Recover interrupted executors explicitly instead of leaving tasks running indefinitely.
- Protect explicit field updates and reject stale GUI asset edits; keep inventory merge semantics separate. Mask credentials by default and require explicit configuration/export disclosure.
- Apply shared header-only separators to every DataGrid, retain native resize grips and node column coordination, and align identical overview tables. Preserve manual widths and measure action buttons to prevent clipped labels.
- Remove observation-age decay from labels, colors, availability counts and anomaly filters. Preserve raw observations/timestamps and actual certificate expiry/reminder deduplication.
- Clarify independent GUI use and Agent enhancement in both READMEs. Add CLI contract tests and real Xray cross-process cancellation/crash tests; document actual validation boundaries.

## 0.1.10 — 2026-11-21

- Update node progress and observations in place instead of rebuilding the page at task start, progress, completion or periodic refresh.
- Share column widths, native resize/reorder state and horizontal scrolling across VPS groups; retain manual widths through filtering, navigation and resizing.
- Add subtle interior header separators using the existing theme border brush, without adding data-row grid lines or replacing native resize grips.
- Open reusable, read-only left/right configuration windows owned by duplicate review; retain the review and main page, and close old views when switching pairs or closing review.
- Clarify that left/right reflect local ID ordering, show actual sources and distinguish missing information from conflicts. Defer association while known identity or route conflicts remain.
- Preserve both complete pre-association configurations as encrypted source snapshots, union notes and history, and retain existing metadata while filling missing fields. No database schema migration or external configuration writes.
- Keep changed source routes separate on subsequent sync. Add WPF layout/window lifecycle, association/backup and real loopback proxy stability regression coverage.
- Reuse the unchanged v0.1.9 screenshots. Validation scope: [validation-0.1.10.md](docs/validation-0.1.10.md).

## 0.1.9 — 2026-11-20

- Add an explicit persisted, off-by-default scheduled inspection switch, including legacy settings migration.
- Preserve queue processing, manual probes, automatic group following and history maintenance while periodic inspection is disabled.
- Unify node text hover/double-click navigation, retain selection/copy, and hide inactive selections.
- Add per-node latency/download actions through the existing runner with busy feedback and duplicate prevention.
- Reorganize settings, preserve exact byte limits when displaying MB, theme all password fields, and show integer latency without changing stored precision.
- Improve node column sizing, task center layout and shared font sizing. Place download bars inside the name column to avoid stale row-detail extents.
- Introduce the approved Cable-N SVG and 10-size Windows icon, consistent version branding and per-monitor DPI manifest.
- Rename the public repository to cloud-mk/VPSnode, preserving history and v0.1.8 assets.
- Add scheduler/settings/UI regression coverage and opt-in loopback Xray button integration tests.

Validation scope and remaining limits: [validation-0.1.9.md](docs/validation-0.1.9.md).

## 0.1.8

Initial Apache-2.0 public release: expanded inventory, multi-source identity matching, encrypted backups and batch inspections. Original release and artifacts retained.
