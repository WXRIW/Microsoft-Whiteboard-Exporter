# Microsoft Whiteboard 导出器

备份 Microsoft Whiteboard、浏览已有导出文件，并从本地 JSON 与资源重新绘制画板。支持个人及组织账号，在微软官方网页中完成登录和多因素认证，无需注册 Entra 应用。

使用 **WPF + .NET 10 + WebView2 + 本地 JavaScript 渲染器**，优先在 Windows ARM64 编译运行，同时提供 x64 发布配置。运行软件无需 Node.js、Python、Office 或 Visual Studio。

## 功能

- **批量备份**：读取白板列表、搜索、多选、取消、失败重试和中断后继续；保存官方 ZIP、可获取的源响应及 `.wbarchive` 备份。
- **离线重绘**：导入官方 ZIP、`.wbarchive` 或 `scene.json` 与资源；支持笔迹、高亮、文字、便签、图片、形状、连接线、分组、列表、表格、链接卡及导出包中的评论。
- **多格式导出**：原始备份、JSON 与资源、PDF、PNG、JPEG、SVG、自包含离线 HTML。
- **整板输出**：PDF 默认按画板比例生成动态纸张单页；图片按画板尺寸与所选比例输出，宽高任一轴超过 20000 像素时自动等比缩小，每种图片格式只生成一张完整图片。
- **应用内预览**：缩放、平移、适应窗口及 PDF／图片查看；打开已有目录后，将同一白板的多种格式归并为一个条目。
- **账号隔离**：不同账号使用独立目录和任务记录，同名或同 ID 的白板不会跨账号合并。

## 环境要求

- Windows 11，本机 ARM64 为优先验证环境，另支持 x64 发布。
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)；自包含包仍需要系统安装此运行时。
- 从源码构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，`global.json` 选择 .NET 10 SDK 系列。
- 使用 Visual Studio 时，安装 **Visual Studio 2026** 和“**.NET 桌面开发**”工作负载。命令行构建不需要 Visual Studio。

首次恢复依赖需要连接 NuGet。Node.js 仅用于可选开发测试，不是软件运行依赖。

## 在 Visual Studio 中运行

1. 打开根目录的 `WhiteboardExporter.slnx`，等待 NuGet 恢复完成。
2. 右键 **WhiteboardExporter.App**，选择“设为启动项目”。Core 和两个 Tests 项目不是桌面应用启动入口。
3. 选择 Debug 或 Release，按 F5 调试，或按 Ctrl+F5 启动。

仓库忽略规则排除构建产物、开发过程记录、浏览器配置及私人导出资料。

## 命令行构建与运行

在仓库根目录的 PowerShell 中执行：

```powershell
dotnet restore .\WhiteboardExporter.slnx
dotnet build .\WhiteboardExporter.slnx -c Release --no-restore
dotnet run --project .\src\WhiteboardExporter.App\WhiteboardExporter.App.csproj -c Release --no-build
```

也可使用现有脚本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1 -NoBuild
```

## 基本使用

1. **试用或导入**：点击“示例白板”体验离线重绘，或“导入文件”选择本地资料。“打开目录…”可以浏览导出总目录、账号目录或单张白板目录。
2. **登录和读取列表**：点击“微软网页登录”，完成登录后点击“读取白板列表”。成功读取后移除演示项，列表使用白板 ID 区分同名画板。
3. **导出**：搜索并使用 Ctrl／Shift 多选，设置目录、格式和图片比例，点击“导出选中白板”。离线 HTML 和 PNG 透明背景默认勾选，可以关闭；JPEG 使用白底。
4. **检查与预览**：查看“内容检查”，切换原始重绘、PDF、图片、HTML 或 SVG。已有目录按需加载，没有原始数据但存在媒体文件时仍可预览。
5. **离线使用**：保存完整 `.wbarchive`，或保留 JSON 与 `assets` 目录。退出账号、断网后仍可导入、重绘并再次导出。

PDF 可选 A4／A3 整板单页或 A4 分页。图片默认 100%，按画板 CSS 像素尺寸输出，可选 10%–1000% 多档比例。导出不受预览窗口、缩放或平移影响。大图预览解码长边最多为 4096 像素，但尺寸提示显示原文件大小，导出原图不会因此缩小。

## 文件、账号与重复导出

```text
导出根目录/
  账号标签__账号键前16位/
    account.json
    export-state.json
    白板标题__白板ID/
      白板标题__白板ID.wbarchive
      白板标题__白板ID.json
      白板标题__白板ID.pdf
      白板标题__白板ID.png
      白板标题__白板ID.html
      assets/
      source/
```

文件按所选格式生成；JPEG、SVG 也保存在该白板目录。原始 ZIP 独立保留，`.wbarchive` 包含原始数据、公开场景、资源与诊断，详见 [格式说明](docs/SCENE-FORMAT.md)。

- 账号通常自动识别，无需打开账号弹窗；个人／组织账号及组织租户分别建立稳定目录键。无法识别时填写该账号的独立目录名称。
- 手工归属升级为自动识别账号时，重新获取该账号数据，旧备份保留；不同账号的相同白板 ID 使用不同目录和任务记录。
- 同账号按白板 ID 复用目录，白板改名不会生成重复目录。内容版本、设置、完成状态及 SHA-256 校验一致时复用已有结果。
- 源包、资源或场景变化时，替换前保留备份／JSON 历史版本。最后编辑时间与采集时间分别保存，未知编辑时间不使用导出时间代替。
- 单种格式失败不会阻止其余格式导出；取消或写入失败保留旧文件。重启后可读取列表或打开备份，再重试失败任务。

打开已有目录只读取原文件，不迁移旧平铺目录，也不会为了预览生成新备份。只有 PDF／图片／SVG／HTML 的条目可以浏览，重新绘制与多格式导出需要原始备份或 JSON。

## 支持边界

“完整”表示已识别对象和资源可绘制且没有未解决的解析警告；缺图、图片加载占位、未知对象或其他警告标记为 `partial`，保留原始内容及原因；无法形成可重绘画布时标记为 `failed`。这些状态不代表已与真实在线画板逐像素对照。

官方 ZIP 可以导出为空画布，这只说明源包没有绘制对象，不能证明在线画板为空。首次补齐缺失资源需要网络，账号图片还需要有效登录。原生 `.whiteboard` 文件可保留，但不承诺任意 Fluid 存储的直接离线解析。

在线访问及导出取决于微软服务、账号权限与租户策略；网页或导出格式变化可能影响采集。SVG 富文本使用 `foreignObject`，部分绘图软件不能显示，分享阅读可使用 PDF 或 PNG。测试方法见 [测试指南](docs/ACCEPTANCE.md)，微软应用退役及在线数据期限以 [微软公告](https://support.microsoft.com/en-us/whiteboard/retirement-standalone-microsoft-whiteboard-apps) 为准。

## 生成可运行包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

默认生成两套包含 .NET 运行时的目录及 ZIP：

```text
artifacts/publish/win-arm64/WhiteboardExporter.exe
artifacts/publish/win-x64/WhiteboardExporter.exe
artifacts/releases/WhiteboardExporter-win-arm64.zip
artifacts/releases/WhiteboardExporter-win-x64.zip
```

解压完整 ZIP 后运行 `WhiteboardExporter.exe`，保留 `Web`、`samples` 等随包文件，不要只复制 EXE。只发布 ARM64 可追加 `-Runtimes win-arm64`；追加 `-FrameworkDependent` 可生成需要目标机器安装 .NET 10 桌面运行时的版本。上述路径由脚本在本机生成，发布脚本同时更新 `SHA256SUMS.txt`。

生成仅包含源码、配置、公开样例和文档的压缩包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-source.ps1
```

产物为 `artifacts/releases/WhiteboardExporter-source.zip`。`.gitignore` 排除本机构建与私人数据，`.gitattributes` 管理 Git 换行，`.editorconfig` 管理编辑器格式；它们会随源码包一起提供。

## 测试

```powershell
# 核心导入、备份和安全检查
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1 -CoreOnly

# 加上系统图片编码与实际 WebView2 桌面冒烟
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1

# 可选：JavaScript 几何、云端就绪、日期和账号识别测试
node --test .\tests\js\geometry.test.mjs .\tests\js\cloud-readiness.test.mjs .\tests\js\cloud-metadata.test.mjs .\tests\js\cloud-account.test.mjs
```

完整测试需要可用 Windows 桌面和 WebView2，默认产物写入 `artifacts/smoke`，不会自动登录微软账号。测试方法见 [测试指南](docs/ACCEPTANCE.md)，原生图片编码独立测试见 [ImageTests 说明](tests/WhiteboardExporter.ImageTests/README.md)。

高级回归入口使用 `WhiteboardExporter.exe` 启动：

| 参数 | 用途 |
| --- | --- |
| `--verify-import 清单.json 输出目录` | 按绝对文件路径 JSON 数组导入、重绘、导出与预览 |
| `--verify-library 已导出目录 验证输出目录` | 只读浏览并核对源文件哈希及修改时间；验证输出必须在原目录之外 |
| `--verify-cloud-metadata 输出目录` | 仅读取已登录账号身份和编辑日期，不打开或采集白板 |
| `--verify-cloud 白板ID清单.json 输出目录` | 使用已有登录会话，按白板 UUID 数组采集并检查各格式；交互登录由账号持有人完成 |

验证输出可能包含私人白板内容和账号清单。元数据主报告不记录身份值，但 `verified-account-directory/account.json` 保存真实账号姓名与邮箱，因此整个输出目录仍属于私人资料。

## 项目结构与文档

```text
WhiteboardExporter.slnx           Visual Studio／dotnet 解决方案
src/WhiteboardExporter.App/       WPF 界面、WebView2、在线采集与本地渲染器
src/WhiteboardExporter.Core/      场景、资源、备份、任务与目录索引
tests/                           核心、原生图片及 JavaScript 测试
samples/                         手工编写的演示与导入样例
scripts/                         构建、启动、发布与测试脚本
docs/                            格式、测试指南及第三方许可资料
```

- [SceneDocument v1 与 `.wbarchive` 格式](docs/SCENE-FORMAT.md)
- [最小独立重绘示例](docs/minimal-redraw.html)：浏览器选择 JSON 和资源，无需运行本软件
- [测试指南](docs/ACCEPTANCE.md)
- [第三方依赖、参考项目与许可证说明](THIRD-PARTY-NOTICES.md)

## 隐私与许可

登录页面与离线预览分开使用浏览器配置。导入 HTML／SVG 移除脚本和事件并阻止远程资源；账号识别仅读取当前账号公开资料，不读取密码或令牌，不将认证 Cookie、认证请求头或令牌写入新增备份和日志。

备份及导出文件可能包含白板内容、图片和公开账号资料，分享或提交前请自行检查；私人验证目录、浏览器配置和导出文件不应纳入源码仓库。示例均为手工测试数据。

本项目是独立工具，不是微软官方应用。项目自身许可证尚未指定；第三方组件及参考项目的授权说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
