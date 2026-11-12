# Delphi 项目管理器

原生 **C# / WinForms / .NET 10** Windows 桌面程序，用于管理和编译 Delphi 项目。应用源码位于 `DelphiBuilder/`，不依赖 VS Code、Node.js 或 WebView。旧 VS Code 插件 `VSCodeVersion/` 仅作为本地参考，已从本仓库排除。

[源代码](https://github.com/1847bell/DelphiProjectManager) · [下载与发行说明](https://github.com/1847bell/DelphiProjectManager/releases) · [问题反馈](https://github.com/1847bell/DelphiProjectManager/issues) · [GPL-3.0 许可](LICENSE)

## 下载产物与运行条件

本地发布目录：`artifacts/publish/DelphiBuilder-win-x64/`。运行其中的 `DelphiBuilder.exe`；**分发时保留整个目录，不能只复制 EXE**。

正式发布的 ZIP 从 [GitHub Releases](https://github.com/1847bell/DelphiProjectManager/releases) 下载；草稿仅仓库维护者可见，不是公开发行版。升级前关闭程序，覆盖应用文件时保留原有 `delphi-builder.db`。

- Windows x64。
- 预先安装 **.NET 10 Desktop Runtime x64**（不是仅 .NET Runtime，也不需要 SDK）：[官方下载](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)。发布产物不包含 .NET 运行时。
- 编译需要合法安装并配置 Delphi XE7 / BDS 15.0。程序不附带 Delphi 工具链。
- 将程序解压到当前用户可写的目录，例如 `D:\Tools\DelphiBuilder`；不建议放到不可写的 `Program Files`。

数据库固定在程序目录 `delphi-builder.db`，不依赖当前工作目录。工作区、扫描到的项目、活动配置/平台、偏好和顺序、设置、输出路径历史及显式保存的窗口布局均存入 SQLite。SQLite 的本机 DLL 是数据库依赖，**不是 .NET 运行时**。

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

- 点击项目后，右上显示版本号、输出路径、启用配置及平台。在上方偏好和下方项目列表之间往返，即使点回原先仍被选中的偏好项目，也会重新激活其详情；鼠标单击/双击、Tab/Shift+Tab 返回偏好和方向键选择均可切换。重复激活当前项目不会覆盖未保存编辑。
- 切换 `Debug` / `Release` 等实际配置后，版本和输出路径随之更新；所选配置/平台立即成为该项目的当前编译目标，并保存到数据库，不修改 `.dproj` 的默认 `Config`。
- 版本号、输出路径编辑后点击 **保存修改**才写入 `.dproj`，只针对当前配置和平台。版本支持 1–4 个数字分量，每段 `0–65535`。
- 相对输出路径以 `.dproj` 所在目录为基准；没有配置输出目录时显示 `.`，表示项目目录。下拉框提供近期保存路径，也可通过 **浏览…**选择目录。
- 切换项目、配置或退出时，如有未保存修改，会询问保存、放弃或取消。
- 保存前验证修改实际生效、其他配置/平台不受影响；保留原 XML 编码、换行和无关元数据。发现外部修改或无法安全隔离的项目格式时拒绝覆盖。
- 保存优先原位更新当前配置/平台的已有属性组，不会因为后面列出 Win64、Debug 等其他目标而追加覆盖组。旧重复组中的过期版本/输出定义会在安全时清理，保留无关属性、注释和公司/自定义版本元数据；含属性引用依赖的旧赋值保留，避免改变顺序求值。只有确有后置共享覆盖或无法隔离继承时，才建立独立的当前目标组。
- **在资源管理器中打开**定位当前 `.dproj`。

### 偏好项目

偏好文件夹和项目**绑定当前工作区**，切换工作区时同步切换偏好列表。不同工作区可以创建同名文件夹，内容和顺序各自独立。中间上半区可 **新建文件夹**、重命名、移除；选中文件夹后使用 **上移/下移**调整当前工作区内文件夹顺序，选中其中的项目则调整该文件夹内项目顺序，顺序保存到 SQLite。

在右侧点击 **添加偏好**，只能选择当前工作区的文件夹。点击偏好项目会激活当前工作区内的项目详情并清空过滤条件。同一文件夹不会重复添加同一项目；移除偏好不删除项目文件。没有工作区时偏好操作不可用。

升级时自动迁移旧数据库，无需重建工作区。旧全局文件夹按其中项目所属工作区拆分，保留文件夹名称、项目引用和相对顺序；旧空文件夹归上次活动工作区，活动记录失效时归第一个工作区，完全没有工作区时保留到首次添加工作区。迁移在事务内完成，无法完整保留项目引用时回滚。升级前请先退出程序并备份数据库。

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

界面固定中文。原插件的多语言、编辑器折叠和 VS Code 菜单设置不适用于本程序，未移植。

## 支持边界

- 当前只声明支持 Delphi XE7；本轮实际编译验证为 **XE7 Win32**。已实现 Win64 目标和参数路径，但本机未发现 DCC64，本轮未运行 Win64 实际编译。
- 使用直接 DCC 编译，固定 `--no-config`，不是完整 MSBuild 引擎。不执行导入的 targets、自定义 Target、编译前后事件、Choose、属性函数或项表达式；遇到这些构造会输出警告，不能依赖其构建效果。
- `RcItem` 的 IDE/MSBuild 资源生成语义不由直接 DCC 流程重建，会明确警告。
- .NET 运行时不随程序分发；小发布目录不代表无需安装桌面运行时。原生 WinForms 避免浏览器内核常驻开销，但不能只凭语言断言所有场景内存都更低。
- 工作区路径以绝对路径保存；移动程序不影响数据库位置规则，但移动 Delphi 项目后需要重新添加/扫描工作区。
- 备份时先退出程序，再复制 `delphi-builder.db`；运行期间可能存在 SQLite WAL/SHM 文件。

## 开发与发布

需要 .NET 10 SDK，在仓库根目录执行：

```sh
dotnet restore DelphiBuilder.slnx
dotnet build DelphiBuilder/DelphiBuilder.csproj -c Release
dotnet test DelphiBuilder.Tests/DelphiBuilder.Tests.csproj -c Release --filter FullyQualifiedName~WorkspaceFavoritesTests
dotnet test DelphiBuilder.Tests/DelphiBuilder.Tests.csproj -c Release --filter FullyQualifiedName~ProjectWriterTests
dotnet run --project DelphiBuilder/DelphiBuilder.csproj

dotnet publish DelphiBuilder/DelphiBuilder.csproj -c Release -p:PublishProfile=Portable
```

也可在 Visual Studio 中打开 `DelphiBuilder.slnx`，使用 `Portable` 发布配置。工程和发布配置均固定 `SelfContained=false`；不使用 WebView、不捆绑 Node 或 .NET。

原生图像资源位于 `DelphiBuilder/Assets/`；程序 ICO 含 16/24/32/48/64/128 像素图像。GitHub 标识使用 [GitHub 官方素材](https://github.githubassets.com/images/modules/logos_page/GitHub-Mark.png)。图像均嵌入程序，分发不需要额外图片文件或运行时下载。

### GitHub Actions 与草稿发行

- `.github/workflows/build.yml` 在推送 `main`、提交针对 `main` 的 Pull Request 或手动运行时，使用 Windows 和 .NET 10 还原、构建、运行原生回归测试，并上传 `DelphiBuilder-win-x64.zip`。
- 推送 `v*` 标签，或在 Actions 的 **Run workflow** 中填写 `tag`（例如 `v1.0.0`），会在构建通过后创建或更新对应的 **Release 草稿**，附上相同 ZIP；留空 `tag` 只构建。标签必须为项目版本前加 `v`。
- 自动化不会公开发布 Release，也不会修改已公开的 Release；维护者检查草稿后手动发布。发行说明位于 `.github/release-notes.md`，发布新版本时与项目版本、变更记录一起更新。
- ZIP 包含应用、SQLite 依赖、README、CHANGELOG、LICENSE 和 THIRD-PARTY-NOTICES.txt，不包含旧插件、个人数据库或 .NET 运行时。CI 不执行依赖本机 Delphi 安装的真实编译和 GUI 操作。

## 版权与开源许可

**Copyright (C) 2026 AlexNiu**

本项目采用 **GNU General Public License v3.0（GPL-3.0-only）**，完整条款见 [LICENSE](LICENSE)。你可以在 GPLv3 条款下使用、研究、修改和再分发本软件；再分发时须保留版权与许可声明，并按 GPLv3 的要求提供相应源代码。软件按“现状”提供，不附带任何明示或默示担保，具体以许可证全文为准。

第三方依赖和素材保留各自的版权、许可与商标权利；随包分发的 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 保留 Microsoft.Data.Sqlite 的 MIT、SQLitePCLRaw 的 Apache-2.0 许可全文及素材归属。GPLv3 声明不改变这些权利，也不授予 Delphi 或 GitHub 商标许可。Delphi 工具链不随本项目分发，使用者须自行取得合法授权。


## 已完成的本地验证

- Release 构建：0 警告、0 错误。
- 隔离数据及实际 WinForms 操作：41 项行为断言通过，覆盖扫描/重扫、SQLite 重开、配置切换与隔离保存、旧编码保存、整串过滤、偏好和设置、日志清空及实际编译。
- 追加验证：现有图标/其他资源字节保留、真实 BRCC 失败阻断 DCC、分段 CP936 stdout/stderr 解码、活动取消终止进程树。
- 本机真实 XE7 Win32 编译成功；生成 DLL 的文件版本及公司元数据已读取验证。编译失败退出码已验证。
- 发布 EXE 已独立启动，确认数据库生成在 EXE 目录、正常关闭、未包含 .NET 运行时文件。
- 发布程序空工作区启动时观测：私有内存约 **20.5 MiB**，工作集约 **75.2 MiB**；程序目录约 **2.59 MiB**。这是本机单次观测，不是与 VS Code/WebView 的受控性能对比，也不包括 DCC 子进程峰值。
- 工作区偏好定向回归：5 项通过，覆盖同名文件夹与排序隔离、跨工作区引用拒绝、工作区移除隔离、旧混合偏好迁移及失败回滚。
- 实际 WinForms 偏好验证：3 次独立进程启动、27 项断言通过（120 DPI），覆盖新建、重命名、添加偏好、排序、工作区切换、清空过滤后激活及重启恢复；截图位于 `artifacts/favorites-proof/`。
- 偏好/列表往返定向验证：真实鼠标和键盘操作共 41 项断言通过（120 DPI），覆盖连续 4 轮往返、原节点复点/双击、Tab/方向键、过滤清空，以及取消保留编辑和每次切换只弹一次确认；日志和截图位于 `artifacts/focus-proof/20261112-114121/`。
- 图标/版权页脚验证：17 项真实窗口断言通过（120 DPI），覆盖主窗口与任务栏图标、默认/最小/最大化布局、长状态不遮挡及实际默认浏览器打开指定主页；真实 EXE 的 6 个 Win32 图标帧与 ICO 数据一致，截图和日志位于 `artifacts/identity-proof/20261112-150540/`。
- dproj 保存定向回归：9 项通过，使用隔离临时文件验证交替/重复保存不新增重复组、旧重复定义清理、完整中文元数据、无关字段/注释与其他目标保留、后置共享覆盖、顺序属性依赖、命名空间及 CP936/UTF-8 BOM/CRLF 保留。
