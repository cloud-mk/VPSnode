# v0.1.11 GUI / CLI 覆盖与缺陷分析

范围：基于 v0.1.10 源码逐一检查页面按钮、表单、任务、设置及共享组件。业务均复用 VPSnode.Core；主题呈现、导航、拖动窗口、文字选择、图表渲染、二维码显示及打开浏览器等纯界面行为无需 CLI 模拟。下表的“提供”表示入口与共享实现存在；实测通过范围另见 [验收记录](validation-0.1.11.md)，不等于所有公网部署场景通过。

## 业务对应矩阵

| GUI 业务 | v0.1.10 CLI | v0.1.11 CLI | 复用实现 / 实测路径 |
| --- | --- | --- | --- |
| VPS 添加与批量导入 | 仅 import 合并 | asset add host；import preview/apply | Store / Validation / Import；实际子进程增删改 |
| VPS 编辑、备注、SSH/密码/密钥路径 | 缺精确编辑 | asset update host；sshPassword 走 stdin | AssetEditing；DPAPI；修订冲突测试 |
| VPS 删除、解除子资产归属 | 缺 | asset delete host | Monitor.Delete / Store.Delete |
| SSH 别名发现和导入 | 缺 | ssh aliases / ssh import | SshInventory / ImportSshAlias；别名匹配共用 GUI 逻辑 |
| 主机、硬件、SSH、监听、候选端口查询 | status 部分摘要 | asset get host / status | AgentView / PortPlan；完整字段子进程查询 |
| SSH 检查、完整盘点 | 入队无回执 | check host/inventory ID | 同一 SshInventory；实际回环 SSH 失败路径；未登录生产主机 |
| 分享链接/TXT 节点导入与预览 | 需自行转换 JSON | node import-links；stdin links 数组 | ShareLink.New / Store.Import；TXT 内容可由 Agent 读入数组 |
| 节点编辑、所属 VPS、服务端及关联字段 | 仅 import 来源合并 | asset update node | Normalize / Validation；显式清空、false 和事务更新 |
| 节点删除、暂停/恢复 | 缺 | asset delete node；node pause/resume | Core 编辑/删除；实际 GUI 解除暂停后 CLI 回读 |
| 完整配置、来源、关联前快照与单节点导出 | 缺 | node config/export [--include-secrets] | ShareLink.Fields / ProxyProbe.ClientLink / NodeIdentity.Sources；默认脱敏测试 |
| 重复节点核对和确认关联 | 缺 | node duplicates；node associate KEEP REMOVE | FindDuplicateCandidates / MergeNodes；原有 0.1.10 核对窗口回归 |
| v2rayN 本机配置、分组和内核查询 | 缺 | v2rayn info/groups/cores | V2Sync / FindCore；隔离数据库样例读取 |
| v2rayN 选组同步及自动跟随选择 | 缺 | v2rayn sync --groups；settings followGroups | Monitor.Sync / V2Sync.Apply；来源保留与错误分组测试 |
| 读取 v2rayN 测试 URL | 缺 | settings read-v2rayn-target [--apply] | 读取逻辑提至 V2Sync.PingTarget，GUI/CLI 共用 |
| 网站、证书端点维护与多节点关联 | 仅 import 合并 | asset add/update/delete web | WebProbe.ForStorage/Targets；关联校验、清空与删除清理 |
| 自动 TLS 网站、回落/证书使用关系查询 | status 部分 | asset list/get web；node config；status | 同一 WebProbe.Targets/UsesEndpoint，区分自动/人工回落 |
| 单节点及分组/全库延迟、下载 | 入队无回执 | check node/speed/host-nodes/nodes/speeds | 同一 ProxyProbe / Monitor；实际本机 Xray、流量上限与失败/超时 |
| REALITY 三路径诊断 | 入队无回执 | check sni ID | 本机 TLS、VPS 握手目标及真实代理的原有组合；公网三路径本轮未验证 |
| 网站、DNS、HTTP80/HTTPS443、自定义 URL、TLS | 入队无回执 | check web ID / websites；check tls ID | 同一 WebProbe；没有另建 HTTP 或检测系统 |
| 最近结果、检测时间、历史 | status 最近摘要 | status；history KIND ID；task list/get | Store.Latest/History；保留原始时间与证书 expires |
| 全部业务设置读写保存 | 缺 | settings get/set | 同一 Settings 与 Core 校验；GUI 保存调用显式事务更新 |
| 任务提交、进度、取消、历史 | check 只有 queued:true | check；task list/get/wait/cancel；worker | Core 持久任务、Monitor 和执行锁；实际双向取消与中断恢复 |
| 台账位置和资产计数 | 缺 | data info；asset list；status | 同一 Store |
| 加密备份、预览、合并/替换恢复 | 缺 | backup export/preview/restore --mode | 同一 Backup.cs；实际进程往返、错口令和完整旧回归 |

## 原有问题与原因

1. **CLI 回执断链。** requests 只保存待消费的类型/目标，执行后删除；CLI 不返回标识；GUI 的 Tick 是唯一消费者，并在本机离线时提前返回。仅打印 queued:true 无法证明任何检查执行。
2. **任务状态只在 GUI 内存。** SemaphoreSlim 只约束一个 Monitor 实例；运行进度与取消依赖事件和进程内 CTS。任务历史仅作为 task/history 观察保存，缺少独立可查询回执。
3. **导入不是编辑。** import 的来源合并会保留空字段和原备注，无法可靠表达删除关系或清空文本；增加同功能 CLI 必须复用明确的业务更新，而非再写一个独立导入系统。
4. **查询投影过少。** 原 status 不返回足够 SSH、监听、配置字段、来源、关联、设置及历史；Core 和 GUI 已有这些数据，缺在 CLI 投影和命令路由。
5. **表头样式局部生效。** 0.1.10 仅在节点列宽控制器里指定右边框，其他 DataGrid 继续使用默认表头。共享样式需绑定表头本身的 DisplayIndex，不能依赖初始化时尚未就绪的 Column 属性。
6. **状态、统计和颜色共同衰减。** IsStale 同时改变状态文案和可用计数，StatusPill 还特判“已过时”。只删除文案会留下统计与配色错误。
7. **独立执行的管道继承问题。** 本轮真实子进程测试发现，普通后台 Process 启动会继承 Agent 捕获输出的句柄，提交进程虽退出，ReadToEnd 仍被 worker 拖住。改用隐藏 ShellExecute 启动，并用并发 ID、等待超时和双向取消实测验证。

## 实际修改位置

- **Core/InspectionTask.cs、Monitor.cs**：任务作为既有加密资产持久化；稳定 ID、状态/进度/错误、去重、取消、执行 PID、旧队列迁移及异常中断恢复；文件独占锁与进程内 gate 配合，Windows 负责崩溃释放。GUI、CLI 和定时巡检共用检测路径。
- **Core/AssetEditing.cs、Validation.cs、V2Sync.cs**：显式事务字段更新、乐观修订校验、SSH 密码保护、关系验证、设置校验、共用别名匹配和读取目标。现有 InventoryDocument 来源合并语义保留。
- **Core/AgentView.cs、CLI/Program.cs**：完整业务命令、结构化查询与帮助、默认敏感字段遮罩、显式原配置导出、退出码及 detached worker。
- **Core/Store.cs、Store.Identity.cs、Backup.cs**：任务保留期清理、资产删除取消、重复关联和备份恢复时重映射任务目标/项目关系。复用原加密备份容器，没有另建存储格式。
- **Core/ObservationState.cs、Desktop 状态/任务/设置文件**：记录年龄不再改变结果，证书有效期仍动态判断并按阈值去重提醒；桌面轮询外部进度，保存时核对修订。
- **Desktop/App.xaml、HeaderSeparator.cs、SharedTableColumns.cs、Ui.cs**：统一表头细线，保留原模板和拖宽热区；同字段表协调宽度，原节点列宽/列序/滚动控制器继续使用。操作列按文字实测宽度设最小值。
- **tests 与 CI**：新增实际 CLI 子进程契约测试；扩展真实 Xray、跨进程取消/崩溃与全页表头/旧状态测试。测试资料均为示例地址、域名和虚构身份。
- **README 中英文、Agent 指南、CHANGELOG、构建版本**：突出普通用户独立使用与 Agent 增强，版本统一为 0.1.11，保留既有公开历史。

不增加网络监听端口、不引入常驻系统服务、不打包新的代理内核、不接管远端部署。验证和限制以验收记录为准。
