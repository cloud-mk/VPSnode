# Agent 资产导入格式

通过标准输入发送 UTF-8 JSON。顶层 version 为 1，hosts、nodes、websites 均为可省略数组。ID 是稳定的本地标识，允许 ASCII 字母、数字、点、减号和下划线，以字母、数字或下划线开头。同一 ID 重复导入更新同条记录。

- hosts 包含 id、name，以及可选 address、ipv6、sshAlias、sshPort、user、keyPath、hostname、hardware、os、provider、tunnelPort、panelPath、notes、ports。监听端口对象含 protocol、address、port、service。只有精确核实的 SSH 别名或地址才能用于盘点。
- nodes 包含 id、name，可选 hostId、link、inboundId、protocol、transport、security、port、vpsVerified、server、related。link 为完整客户端分享 URI；只有服务端资料时留空，节点显示待补。server、related 为字符串字典。仅在实际核对后填 vpsVerified。REALITY 目标字段可用于 VPS 侧一键诊断。
- websites 包含 id、name，可选 hostId、domain、port、url、connectAddress、sni、location、expectedStatus、contains、checkWebsite、fallbackNodeIds、certificateNodeIds。fallbackNodeIds 是实际的回落节点关联。certificateNodeIds 记录使用该证书域名的节点。共享入口可以关联多个节点。port 是证书端点端口。网页检查分别从 80 和 443 访问域名，额外 URL 可单独使用规则。
- 预览和应用返回 added、updated、conflicts。无效 JSON 或无法引用的父资产返回非零退出码。status 仅返回脱敏摘要。在 Windows 上运行的桌面进程处理 check 发送的本地请求。
