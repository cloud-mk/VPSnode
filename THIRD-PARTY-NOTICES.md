# 第三方依赖说明

VPSnode 采用 Apache License 2.0，作者 cloud-mk，详见 LICENSE 和 NOTICE。以下组件保留各自的许可；具体版本以项目文件和 NuGet 还原结果为准。

VPSnode is licensed under Apache-2.0 by cloud-mk. Dependencies retain their respective licenses. See LICENSE, NOTICE, and the bundled licenses directory.

| 依赖 | 许可 | 项目 |
| --- | --- | --- |
| .NET、WPF、Windows Forms、Microsoft.Data.Sqlite、System.Security.Cryptography.ProtectedData | MIT | https://github.com/dotnet |
| SQLitePCLRaw | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| DnsClient.NET | Apache-2.0 | https://github.com/MichaCo/DnsClient.NET |
| QRCoder | MIT | https://github.com/Shane32/QRCoder |
| SQLite 原生引擎 | Public domain | https://sqlite.org/copyright.html |

DnsClient 1.8.0：Copyright (c) 2024 Michael Conrad。SQLitePCLRaw 3.0.5：Copyright 2014-2025 SourceGear, LLC。QRCoder 1.7.0：Copyright (c) 2013-2025 Raffael Herrmann；Copyright (c) 2024-2025 Shane Krueger。

随包 `licenses` 目录包含 [Apache License 2.0 原文](licenses/Apache-2.0.txt)、[QRCoder MIT 原文](licenses/QRCoder-LICENSE.txt) 及构建实际使用的 .NET 运行时 LICENSE/THIRD-PARTY-NOTICES 文件。VPSnode 使用用户现有 v2rayN 内核，不分发 Xray 或 sing-box。
