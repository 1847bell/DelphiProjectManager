# AGENTS.md

## 项目与范围

- 独立原生应用位于 `DelphiBuilder/`：C# / .NET 10 WinForms；`VSCodeVersion/` 是仅保留在本地、由 Git 忽略的旧 TypeScript 原型，不属于本仓库发布内容。
- 应用读取 Delphi `.dproj`，直接调用 `DCC32.exe` / `DCC64.exe`，提供工作区、偏好、项目设置和编译日志。
- 当前实现和真实编译验证的版本仅为 Windows 上的 Delphi XE7 / BDS 15.0。其他版本不能仅凭参数相似就宣称支持。
- 使用说明见根目录 `README.md`，变更记录见 `CHANGELOG.md`；项目采用 `GPL-3.0-only`，版权为 Copyright (C) 2026 AlexNiu，完整条款见 `LICENSE`。

## 原生 C# 应用

- `MainForm.cs` / `MainForm.Layout.cs`：三栏界面、目标切换、项目过滤、偏好和实时日志；`SettingsForm.cs`：中文编译设置。
- `Services/AppStore.cs` / `WorkspaceScanner.cs`：SQLite、稳定项目身份、偏好顺序和安全递归扫描。数据库固定为 `AppContext.BaseDirectory/delphi-builder.db`，不改为用户目录。
- `Services/ProjectService.cs` / `Services/Project/`：`.dproj` 条件/属性求值、原编码保存和配置隔离验证。
- `Services/BuildService.cs` / `Services/Build/`：XE7 环境与参数、资源/版本同步、可取消的实际进程执行。
- 根目录执行 `dotnet build DelphiBuilder/DelphiBuilder.csproj -c Release`；发布使用 `dotnet publish DelphiBuilder/DelphiBuilder.csproj -c Release -p:PublishProfile=Portable`。
- 偏好存储回归执行 `dotnet test DelphiBuilder.Tests/DelphiBuilder.Tests.csproj -c Release --filter FullyQualifiedName~WorkspaceFavoritesTests`；测试数据位于独立临时目录，不访问程序目录数据库。
- dproj 保存回归执行 `dotnet test DelphiBuilder.Tests/DelphiBuilder.Tests.csproj -c Release --filter FullyQualifiedName~ProjectWriterTests`；覆盖交替保存、重复组清理、元数据及编码/目标隔离，不访问用户项目。
- 必须保持 framework-dependent、`SelfContained=false`；不捆绑 .NET、WebView 或 Node。只提供 XE7 配置；本轮仅实际验证 Win32。
- 原生界面固定中文，不引入多语言。筛选保持单个字符串的 `OrdinalIgnoreCase` 连续包含语义，不能拆词。
- 版本和输出写入磁盘前验证目标实际生效且其他目标不变；活动配置/平台仅立即保存到 SQLite，不改 `.dproj` 默认配置。
- 保存优先复用已有目标组；后续互斥配置/平台不是覆盖。清理旧版本/输出定义时保留无关属性、注释及顺序求值依赖，不批量重写 XML。
- 重扫保留稳定 ID、偏好和路径历史；离线根目录不清空列表，不可访问子树保留既有记录。数据库移除操作不得删除磁盘项目。
- 偏好文件夹及其项目必须属于同一工作区；名称唯一性和文件夹排序按工作区限定。加载工作区时同步刷新偏好，旧全局偏好迁移必须保留项目引用和相对顺序。
- 修改 C# 遵循现有四空格风格。原生变更更新根目录说明和变更记录，插件文档仅在插件本身变化时更新。
- README 面向公开用户，介绍功能、安装、使用、兼容性、源码构建和许可；不混入个人环境、会话验收记录、内部发行状态或未随仓库提供的验证产物路径。
- 主窗体/设置行为需用真实 WinForms 窗口验证；编译器行为需实际工具链或定向进程场景证明。不要把类型检查等同于 GUI/编译通过。

## 验证与工作区保护

- 按改动运行最小相关检查。CI 在 Windows 上运行 .NET 10 构建和原生回归；Delphi 编译兼容性声明仍需真实工具链测试，GUI 变化仍需实际 WinForms 验证。
- 文档或 Git 配置变更只需验证相关文件及仓库行为，不因此运行完整集成测试。
- 不手工修改 `bin/`、`obj/` 或其他构建产物。`artifacts/`、本地规划记录、IDE/Agent 状态、数据库及旧 `VSCodeVersion/` 均不提交；不得删除用户的本地原型或个人数据库。
- `.github/workflows/build.yml` 在 main/PR/手动运行时构建便携 ZIP；v* 标签或显式 tag 输入仅创建/更新 Release 草稿，不公开发布或覆盖已发布 Release。打包必须包含 LICENSE，不含 .NET 运行时和个人数据。
- 未经明确要求，不提交、推送、修改远程地址或执行破坏性 Git 操作。
