# Changelog

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
