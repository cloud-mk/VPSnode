# Agent CLI · v0.1.11

VPSnode 是普通用户与 Agent 共用的 Windows 本地资产台账。普通用户可完全通过 GUI 使用；Agent 用现有 SSH、面板和其他工具完成远端工作，再通过本 CLI 记录、核对和检测。CLI 没有网络服务，不执行远端安装或配置变更。

## 环境与输出契约

升级前退出旧版（含托盘）。GUI 与 CLI 使用同版 v0.1.11；旧版本不了解新增的跨进程执行锁。

在与 GUI 相同的 Windows 用户下运行 `vpsnode-cli.exe`。默认数据在 `%LOCALAPPDATA%\VPSnode`；`VPSNODE_DATA` 指向完整共享数据目录。隔离测试请指定另一个空目录，不要使用生产库。DPAPI 数据绑定 Windows 用户；迁移使用加密备份。

正常输出为一份 UTF-8 JSON，顶层含 `protocolVersion: 1`。命令帮助也为 JSON：`help`、`--help` 或 `COMMAND --help`。未知命令、未知/重复选项、错误数值和错误 JSON 返回非零。参数使用独立词项；不支持 `--name=value`。ID 是原有稳定资产标识；不要用显示名称定位对象。

| 退出码 | 含义 |
| --- | --- |
| 0 | 查询/编辑/提交成功，或等待的检测任务通过；提交成功不代表检测通过 |
| 1 | 本地数据库、文件、依赖或执行进程错误 |
| 2 | 参数、JSON 或业务校验失败 |
| 3 | 对象/任务不存在 |
| 4 | ID、修订号或导入来源冲突 |
| 5 | 等待的检测未通过；检查项目明细，包括全部跳过和空目标批次 |
| 6 | 操作或等待的任务已取消 |
| 7 | 仅等待超时；任务继续，可查询或取消 |

错误格式为 `{"protocolVersion":1,"error":{"code":"not_found","message":"…"}}`。检测回执包含 `errorCode`、`error` 和各项目明细。`task get/list` 是查询操作，找到失败任务也返回 0；只有 `check --wait` 和 `task wait` 根据检测终态返回 5/6。

## 资产与配置

```powershell
vpsnode-cli.exe asset list host
vpsnode-cli.exe asset get host demo-vps
vpsnode-cli.exe asset get node demo-node
vpsnode-cli.exe asset list web
vpsnode-cli.exe status
vpsnode-cli.exe status demo-node
```

`status` 返回 hosts/nodes/websites/observations/tasks/settings。资产项含 `asset`、`revision`；主机另含 SSH 密码是否存在及 `portPlan`（监听证据、登记用途和候选），节点另含客户端字段与网站关系，网站另列自动回落来源。观察的 `state`、`expires`、`at` 保持原值；status/history 另加 `effectiveState` 表示当前显示判定，保留真实证书到期判断。

```powershell
'{"name":"示例 VPS","address":"192.0.2.10","sshPort":22,"user":"root"}' |
  vpsnode-cli.exe asset add host demo-vps
'{"name":"示例节点","hostId":"demo-vps","paused":true}' |
  vpsnode-cli.exe asset add node demo-node
'{"notes":"已人工核对","paused":false}' |
  vpsnode-cli.exe asset update node demo-node
vpsnode-cli.exe node pause demo-node
vpsnode-cli.exe node resume demo-node
vpsnode-cli.exe asset delete node demo-node
```

`asset add|update <host|node|web> ID` 从 stdin 读取显式字段对象，字段对应 [资产模型](inventory.md) 和源码 Models.cs。新增须有 name；链接会规范化协议、地址、端口和传输。更新只改提供的顶层字段：`""` 清空文本，`[]` 清除关联，`false` 关闭开关，字典整体替换。null 只用于本来可空的字段。未知、重复和不可写字段被拒绝。不能更改 ID。

主机用 `sshPassword` 输入明文新密码，空字符串清除；传入后即以当前 Windows 用户加密。不要传 `passwordCipher`。SSH 路径只保存引用，不读取私钥内容。可以维护 VPS 盘点/监听资料，但只有真实核对后才填写核实标记和采集时间。

可使用 `asset update ... --revision HASH` 提交查询返回的 revision，冲突返回 4。GUI 主机、节点和已保存网站编辑同样拒绝覆盖期间被修改或删除的记录。多个 CLI 显式更新在事务中读改写，避免省略字段覆盖其他更新。网站可编辑自动列出的证书端点；自动回落不会因此变成人工确认关系。

删除主机保留其节点/网站并转为未归属；删除节点解除网站关联并清理该对象检测历史，取消待执行目标任务。任务回执保留以供核对。远端配置和 v2rayN 原始资料不被删除。

`node config ID` 返回客户端、传输、安全参数、来源和关联。`node duplicates` 列出候选及逐字段差异；`node associate KEEP_ID REMOVE_ID` 对比后确认关联，拒绝明确冲突，保留配置快照和历史并重映射任务/网站。不会因同名、同端口自动认定相同节点。

默认隐藏认证、分享 URI、服务端敏感字段及来源配置快照。仅 `node config ID --include-secrets` 和 `node export ID --include-secrets` 显式显示原配置。后者返回 `inventory` 文档，可用于本地导入。完整输出含凭据，请保存到受保护的本地文件，不贴到对话、公开日志或 Issue。

## 导入、SSH 与 v2rayN

`validate`、`import --preview`、`import --apply` 读取版本 1 InventoryDocument。预览与应用执行同一事务计划；应用仍沿用原有来源合并规则，空字段通常保留旧值。精确清空用 `asset update`。应用会排入原有检查队列并启动 worker；查看 `task list` 可取得对应任务。`asset` 编辑本身不自动检测。

`node import-links [--preview]` 读取 `{"links":["完整分享 URI"],"hostId":"可空"}`，复用同一分享解析、校验及导入事务。分享 URI 放在 stdin，不放命令参数。

```powershell
vpsnode-cli.exe ssh aliases
vpsnode-cli.exe ssh import YOUR_EXISTING_ALIAS
vpsnode-cli.exe v2rayn info
vpsnode-cli.exe v2rayn sync
vpsnode-cli.exe v2rayn sync --groups GROUP_ID
vpsnode-cli.exe v2rayn sync --groups @ungrouped
vpsnode-cli.exe settings read-v2rayn-target --apply
```

SSH 别名来自本机 OpenSSH 配置；导入读取 `ssh -G` 后保存并提交盘点，返回资产 ID 和任务。`check inventory HOST_ID` 进行只读远端采集。成功取决于 SSH、远端 Python 和读取权限；本轮发布验收没有登录生产 VPS。

v2rayN 使用设置中的目录，读取本地配置、分组和已有核心路径。`sync` 默认全部分组，`--groups` 接受逗号分隔 ID，`@ungrouped` 表示未分组；不修改外部数据库。自动跟随通过设置 `followGroups` 保存，由运行中的 GUI 周期触发。CLI worker 只处理已提交工作，不是常驻巡检服务。单独读取目标不加 `--apply` 时不保存。

## 提交 → 进度 → 最终结果

```powershell
$receipt = vpsnode-cli.exe check node demo-node | ConvertFrom-Json
$id = $receipt.task.id
vpsnode-cli.exe task get $id
vpsnode-cli.exe task wait $id --timeout 60
vpsnode-cli.exe task cancel $id
vpsnode-cli.exe task list --limit 100
vpsnode-cli.exe history node demo-node --limit 100
vpsnode-cli.exe history speed demo-node --since 2026-01-01T00:00:00Z
```

`check KIND [ID] [--wait] [--timeout SECONDS]` 返回稳定任务 ID。批量类型：all、nodes（延迟）、speeds（下载）、websites。单对象类型：host（SSH）、inventory（SSH 完整盘点）、host-nodes、node、speed、sni（REALITY 三路径）、web（网站+证书）、tls（仅证书）。批量不传 ID。立即提交返回 `accepted` 和 `task`，不声明检测成功；`--wait` 等待终态，默认 60 秒，可设 1–86400 秒。

状态为 queued / running / completed / failed / canceled；`result.items` 列出每个目标的排队、测试中、成功、失败、超时或跳过原因，`total/succeeded/failed/skipped/remaining` 提供计数。存在失败/超时、全部跳过或空批次的回执为 failed；部分成功、其余有明确跳过时为 completed，仍须阅读明细。`result` 在执行前可为 null。

提交自动隐藏启动短生命周期 worker；GUI 未运行、定时巡检关闭或本机网络不可用都不会让任务无限留在队列。离线检测会产生实际失败结果。worker 与 GUI 复用同一 Monitor、检测器、配置、SQLite、执行文件锁和取消标记。同一类型/目标的活动提交复用 ID，不重测；不同任务串行。支持 GUI 取消 CLI、CLI 取消 GUI。

`worker` 也可显式排空队列后退出。执行进程异常结束时，文件锁由 Windows 释放；下一次 GUI tick、`worker` 或 `task wait` 将遗留 running 任务标为 failed / executor_interrupted，不自动重试。`task get` 为只读快照，不启动恢复。`executorPid` 仅供诊断，取消用任务 ID。

Ctrl+C 取消当前 CLI 操作/等待；已经独立提交的任务继续，可用 `task cancel` 明确取消。等待超时也不取消后台检测。GUI 退出会取消由 GUI 执行的任务，独立 worker 可继续。暂停、来源删除、入站和客户端禁用继续产生真实跳过原因。

最近结果不再按记录年龄降级。证书的真实 `expires` 仍随时间判定过期/临近到期；原始记录与时间戳不改写。历史按设置保留 1–365 天；`history` 最多 5000 条，`task list` 默认 100、最多 5000，已知 ID 可直接查询保留期内回执。旧版 `history task history` 仍可读。

## 设置和数据

`settings get` 读取全部设置；`settings set` 从 stdin 精确更新指定字段。支持 v2Directory、theme、scheduledInspectionEnabled、intervalMinutes（2–1440）、historyDays（1–365）、speedBytes（100000–100000000）、pingUrl/pingStatus（100–599）、speedUrl、followGroups。修改 v2Directory 且没有同时提供 followGroups 时清空旧自动跟随。定时巡检默认关闭，下载始终手动触发。

```powershell
'{"historyDays":90,"speedBytes":1000000}' | vpsnode-cli.exe settings set
vpsnode-cli.exe data info
# 备份口令通过 stdin 提供；实际使用受保护文件/进程 stdin，不写入命令历史。
vpsnode-cli.exe backup export PATH
vpsnode-cli.exe backup preview PATH
vpsnode-cli.exe backup restore PATH --mode merge
vpsnode-cli.exe backup restore PATH --mode replace
```

备份三命令均读取 `{"password":"独立备份口令"}`。预览只读；merge 保留本机同 ID 并检查身份冲突；replace 完整替换。沿用 GUI 的认证加密和事务恢复机制，包括任务回执；外部私钥、OpenSSH 配置和 v2rayN 核心不在备份内。恢复后由 GUI 或显式 worker 处理待执行工作。

## English contract summary

The GUI works independently for beginners. Agents use this local Windows CLI after operating remote services with their existing tools. All operations reuse VPSnode.Core and the encrypted local database; no HTTP service is required. Match the GUI's Windows account and VPSNODE_DATA directory.

Use `asset list/get/add/update/delete`, `node config/duplicates/associate/import-links/pause/resume`, `ssh aliases/import`, `v2rayn info/sync`, `settings get/set/read-v2rayn-target`, `backup export/preview/restore`, and `history`. JSON edits are explicit patches, unlike provenance-aware inventory merges. Credentials go through stdin. Default output is redacted; full config/export requires `--include-secrets`.

`check` returns a durable `task.id` and starts a hidden local worker even with no GUI. Poll `task get`, wait with `task wait`, or cancel with `task cancel`. States are queued, running, completed, failed and canceled. Inspect item results and counts. Submission exit 0 only means accepted; waiting returns 5 for failed checks, 6 for cancellation, and 7 for a wait timeout. Timeout/Ctrl+C while waiting does not cancel an already submitted task. GUI and CLI serialize probes and share cancellation. Interrupted executors are marked failed on the next worker/wait/GUI tick, without automatic retry.

Keep the original observation timestamps and states. Age alone does not downgrade a result; real certificate expiration still applies. See the [coverage matrix](coverage-0.1.11.md) and [validation boundaries](validation-0.1.11.md) before claiming a production check passed.
