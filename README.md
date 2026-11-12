# Delphi Project Manager · Delphi 项目管理器

用于集中管理、配置和编译 Delphi 项目的 Windows 桌面工具。支持多工作区、项目筛选、版本与输出路径管理、偏好项目及实时编译日志，采用 C# / WinForms / .NET 10 开发，无需安装 VS Code、Node.js 或 WebView。

[源代码](https://github.com/1847bell/DelphiProjectManager) · [下载与发行说明](https://github.com/1847bell/DelphiProjectManager/releases) · [问题反馈](https://github.com/1847bell/DelphiProjectManager/issues) · [GPL-3.0 许可](LICENSE)

## 主要功能

- **多工作区管理**：递归扫描 `.dproj`，刷新项目列表，保留项目配置和偏好。
- **快速查找项目**：按项目名称实时过滤，支持大小写不敏感的连续文本匹配。
- **配置与版本管理**：切换配置和平台，单独保存版本号及输出路径，不影响其他目标。
- **偏好项目**：按工作区组织偏好文件夹，调整文件夹和项目顺序。
- **编译与日志**：调用 Delphi XE7 命令行工具，显示实时日志，支持资源处理和取消编译。
- **布局与数据持久化**：保存窗口布局、设置与输出历史，使用程序目录中的 SQLite 数据库。

## 下载与安装

### 运行要求

- Windows x64。
- [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)。运行应用不需要 .NET SDK；发布包不包含 .NET 运行时。
- 使用编译功能时，需要合法安装 Delphi XE7 / BDS 15.0，并配置对应编译工具。程序不附带 Delphi 工具链。

### 安装步骤

1. 从 [GitHub Releases](https://github.com/1847bell/DelphiProjectManager/releases) 下载 `DelphiBuilder-win-x64.zip`。
2. 将 ZIP **完整解压**到当前用户可写的目录，例如 `D:\Tools\DelphiBuilder`。
3. 运行 `DelphiBuilder.exe`。请保留随包提供的 DLL 和许可证文件，不能只复制 EXE。

### 快速开始

1. 点击 **添加工作区**，选择包含 Delphi 项目的目录。
2. 在项目列表中选择项目，确认配置、平台、版本号和输出路径。
3. 如需修改版本或输出路径，编辑后点击 **保存修改**。
4. 点击 **设置**，确认 XE7 编译器和资源工具路径。
5. 点击 **编译**，在日志区查看进度和结果。

## 使用

### 窗口布局

- 默认窗口会按按钮实际宽度分配三栏，空间足够时，工作区、偏好和编译按钮各自保持一行。
- 调整窗口大小、最大化状态、三处分隔条及项目列表列宽后，点击右上角 **保存布局**。下次启动将恢复这份布局。
- 布局保存在同一 SQLite 数据库中；仅显式点击时更新，退出不会覆盖上次保存的布局。
- 不同电脑的分辨率和缩放会影响实际像素尺寸。恢复时按当前 DPI 换算，并将窗口限制在可见屏幕工作区内；屏幕空间不足时按钮仍允许换行。可在目标电脑重新调整后再次保存。
- 右下角显示 **Copyright (C) 2026 AlexNiu**，紧接的 GitHub 图标通过默认浏览器打开 [GitHub 主页](https://github.com/1847bell/)。图标支持鼠标点击，也可用 Tab 定位后按空格激活；长状态文字不会挤掉版权区。

### 工作区与项目过滤

1. 左侧点击 **添加工作区**，选择目录。程序递归扫描所有 `.dproj`，结果写入数据库；可以添加多个工作区。
2. 中间下半区显示当前工作区项目。输入名称即可实时过滤，大小写不敏感。
3. 过滤为**完整字符串的连续包含匹配**。例如 `Alpha Beta` 匹配 `My Alpha Beta App`，不匹配 `Alpha X Beta`；不会拆词。
4. 点击 **更新工作区文件**重新扫描，新增/移除文件同步到项目列表；未改变的项目保留活动配置、偏好和历史。
5. 扫描跳过目录重解析点；不可访问子目录保留已有记录并提示。工作区暂时离线时拒绝清空列表。
6. 左侧 **移除**只删除该工作区的数据库记录、偏好文件夹和项目引用，不影响其他工作区，也不删除磁盘目录或项目文件。

### 项目详情与保存

- 点击项目后，详情区显示版本号、输出路径、当前配置和平台。项目列表与偏好列表均可用于选择项目；重复选择当前项目不会覆盖未保存的编辑。
- 切换 `Debug` / `Release` 等实际配置后，版本和输出路径随之更新；所选配置/平台立即成为该项目的当前编译目标，并保存到数据库，不修改 `.dproj` 的默认 `Config`。
- 版本号、输出路径编辑后点击 **保存修改**才写入 `.dproj`，只针对当前配置和平台。版本支持 1–4 个数字分量，每段 `0–65535`。
- 相对输出路径以 `.dproj` 所在目录为基准；没有配置输出目录时显示 `.`，表示项目目录。下拉框提供近期保存路径，也可通过 **浏览…**选择目录。
- 切换项目、配置或退出时，如有未保存修改，会询问保存、放弃或取消。
- 保存会保留其他配置/平台、原 XML 编码、换行、无关属性及版本元数据，并优先更新已有目标属性组。若项目已被其他程序修改，或无法安全隔离修改范围，会拒绝覆盖并提示原因。
- **在资源管理器中打开**定位当前 `.dproj`。

### 偏好项目

偏好文件夹和项目**绑定当前工作区**，切换工作区时同步切换偏好列表。不同工作区可以创建同名文件夹，内容和顺序各自独立。中间上半区可 **新建文件夹**、重命名、移除；选中文件夹后使用 **上移/下移**调整当前工作区内文件夹顺序，选中其中的项目则调整该文件夹内项目顺序，顺序保存到 SQLite。

在右侧点击 **添加偏好**，只能选择当前工作区的文件夹。点击偏好项目会激活当前工作区内的项目详情并清空过滤条件。同一文件夹不会重复添加同一项目；移除偏好不删除项目文件。没有工作区时偏好操作不可用。

程序会自动迁移已有数据库中的偏好记录，并保留项目引用和相对顺序；迁移失败时回滚。升级前请退出程序并备份数据库。

### 编译与日志

1. 点击右上角 **设置**，配置 XE7 编译器路径。
2. 选择项目、配置及 `Win32` / `Win64`，点击 **编译**。
3. 每次编译清空上一次日志，实时显示 stdout/stderr 并自动滚动。
4. 编译期间禁止切换项目/设置，提供 **取消编译**；取消会终止工具进程树并排空输出。资源编译失败不会继续启动 DCC。

启用资源处理时，会先通过 `BRCC32.exe` 编译 `RcCompile`，为缺失的 `{$R *.res}` 创建有效资源，并将项目版本同步到 `.res`；已有图标和其他非版本资源保留。

## 设置

| 选项 | 说明 |
| --- | --- |
| Delphi 版本 | 当前仅 XE7 / BDS 15.0 |
| Win32 编译器 | `DCC32.exe`；留空尝试自动发现 |
| Win64 编译器 | `DCC64.exe`；目标机器需实际安装此工具 |
| 环境脚本 | `rsvars.bat`；留空尝试从工具链发现 |
| 资源编译器 | `BRCC32.exe`；资源项目需要此工具 |
| 资源处理 | 创建/编译资源并同步版本信息，默认开启 |
| 输出路径历史数量 | 每项目保留 1–15 项，默认 5 |
| 输出编码 | 系统 ANSI 代码页、CP936、UTF-8 |
| 额外 DCC 参数 | **每行一个完整参数**，不按空格拆分；主源由程序添加 |
| 环境变量 | 在发现的 BDS 环境之后覆盖，变量名大小写不敏感 |

应用界面为中文。

## 兼容性与限制

- 当前提供 Delphi XE7 / BDS 15.0 配置，**Win32 编译已验证**。Win64 目标需要 `DCC64.exe`，兼容性尚未完成验证；其他 Delphi 版本暂不支持。
- 使用直接 DCC 编译，固定 `--no-config`，不是完整 MSBuild 引擎。不执行导入的 targets、自定义 Target、编译前后事件、Choose、属性函数或项表达式；遇到这些构造会输出警告，不能依赖其构建效果。
- `RcItem` 的 IDE/MSBuild 资源生成语义不由直接 DCC 流程重建，会明确警告。

## 数据、备份与升级

工作区、项目配置、偏好、设置、输出路径历史及窗口布局保存在程序目录的 `delphi-builder.db`，不依赖启动程序时的工作目录。SQLite 原生 DLL 是数据库依赖，不是 .NET 运行时。

- 工作区路径以绝对路径保存；移动程序不影响数据库位置规则，但移动 Delphi 项目后需要重新添加/扫描工作区。
- 备份时先退出程序，再复制 `delphi-builder.db`；运行期间可能存在 SQLite WAL/SHM 文件。
- 升级时先退出程序并备份数据库，再覆盖应用文件；保留原有 `delphi-builder.db`，不要用其他数据库替换。

## 从源码构建

需要 Windows 和 .NET 10 SDK，在仓库根目录执行：

```sh
dotnet restore DelphiBuilder.slnx
dotnet build DelphiBuilder.slnx -c Release
dotnet test DelphiBuilder.Tests/DelphiBuilder.Tests.csproj -c Release --no-build
dotnet run --project DelphiBuilder/DelphiBuilder.csproj

dotnet publish DelphiBuilder/DelphiBuilder.csproj -c Release -p:PublishProfile=Portable
```

发布文件输出至 `artifacts/publish/DelphiBuilder-win-x64/`。也可在 Visual Studio 中打开 `DelphiBuilder.slnx`，使用 `Portable` 发布配置。工程和发布配置均固定 `SelfContained=false`，不打包 .NET 运行时。

## 版权与开源许可

**Copyright (C) 2026 AlexNiu**

本项目采用 **GNU General Public License v3.0（GPL-3.0-only）**，完整条款见 [LICENSE](LICENSE)。你可以在 GPLv3 条款下使用、研究、修改和再分发本软件；再分发时须保留版权与许可声明，并按 GPLv3 的要求提供相应源代码。软件按“现状”提供，不附带任何明示或默示担保，具体以许可证全文为准。

第三方依赖和素材保留各自的版权、许可与商标权利；随包分发的 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 保留 Microsoft.Data.Sqlite 的 MIT、SQLitePCLRaw 的 Apache-2.0 许可全文及素材归属。GPLv3 声明不改变这些权利，也不授予 Delphi 或 GitHub 商标许可。Delphi 工具链不随本项目分发，使用者须自行取得合法授权。

## 问题反馈

请通过 [GitHub Issues](https://github.com/1847bell/DelphiProjectManager/issues) 报告问题或提出建议。报告编译问题时，请注明应用版本、Windows 与 Delphi 版本、目标配置和平台，并提供复现步骤及相关日志；提交前请移除日志中的敏感路径、凭据和业务信息。
