## Delphi 项目管理器

原生 C# / WinForms / .NET 10 桌面应用，固定中文界面，不依赖 VS Code、Node.js 或 WebView。

### 功能

- 多工作区递归扫描 `.dproj`，项目名称整串实时过滤。
- 配置/平台切换、配置隔离的版本号及输出路径保存、输出路径历史。
- 工作区独立偏好文件夹、项目排序、窗口布局显式保存与恢复。
- 直接调用 Delphi XE7 编译器，实时编译日志、资源/版本处理与取消进程树。
- 修复偏好与项目列表往返激活，以及保存配置时重复追加 `PropertyGroup` 的问题。

### 下载与运行

1. 下载 `DelphiBuilder-win-x64.zip`，解压全部文件到可写目录。
2. 安装 [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)，运行 `DelphiBuilder.exe`。
3. 编译功能需要自行合法安装 Delphi XE7 / BDS 15.0；本程序不附带 Delphi 工具链。

ZIP 不包含 .NET 运行时或用户数据库。升级前退出程序并备份 `delphi-builder.db`，覆盖应用文件时保留该数据库。

兼容性：已验证 Delphi XE7 / BDS 15.0 的 **Win32 编译**。Win64 目标需要 `DCC64.exe`，兼容性尚未完成验证；其他 Delphi 版本暂不支持。

### 源码与许可

Copyright (C) 2026 AlexNiu。项目采用 **GPL-3.0-only**，完整许可证随 ZIP 一并提供；软件不附带担保。第三方依赖和素材保留其各自权利，MIT/Apache-2.0 许可和素材归属见随包分发的 `THIRD-PARTY-NOTICES.txt`。

[源代码与使用说明](https://github.com/1847bell/DelphiProjectManager) · [变更记录](https://github.com/1847bell/DelphiProjectManager/blob/main/CHANGELOG.md) · [GPLv3 全文](https://github.com/1847bell/DelphiProjectManager/blob/main/LICENSE)
