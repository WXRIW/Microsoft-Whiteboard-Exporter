# 测试指南

从源码验证导入、重绘、格式导出、目录浏览和云端采集的方法。自动测试使用合成样本；真实白板的完整性、断网重导出和多账号隔离需使用自己的资料单独核对。

## 环境和测试命令

需要 Windows 桌面、.NET 10 SDK 和 WebView2 Runtime。JavaScript 回归需要开发机安装 Node.js；应用运行不依赖 Node.js。首次恢复依赖需要访问 NuGet。

在项目根目录的 PowerShell 中执行：

```powershell
# 恢复依赖并编译整个解决方案
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1

# Core + WIC/原生文件提交 + Release 桌面冒烟
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1 -OutputDirectory .\artifacts\acceptance\desktop

# 仅运行 Core；不初始化桌面浏览器
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1 -CoreOnly

# JavaScript：直接执行生产代码中的解析与云端读取逻辑
node --test .\tests\js\geometry.test.mjs .\tests\js\cloud-readiness.test.mjs .\tests\js\cloud-metadata.test.mjs .\tests\js\cloud-account.test.mjs
```

WIC 测试可独立运行：

```powershell
dotnet run --project .\tests\WhiteboardExporter.ImageTests\WhiteboardExporter.ImageTests.csproj -c Release -- .\artifacts\acceptance\wic
```

桌面结果在所选输出目录的 `smoke-result.json`；图片编码结果位于对应 WIC 输出目录。检查退出码和报告中的失败、待验收项，并打开生成的 PDF／图片核对。可选的 `scripts/verify-pdf-output.py` 使用 Python、pypdf、pdfplumber 独立读取冒烟 PDF 的页数、纸张和中文文字，不是应用运行依赖。

通过 `scripts/publish.ps1` 生成 ARM64／x64 包后，还应直接运行目标包的 `WhiteboardExporter.exe --smoke-test "独立输出目录"`，避免只验证开发机编译目录。发布脚本默认产物在 `artifacts/publish/<架构>`，便携包在 `artifacts/releases`。

## 四类专用验收入口

以下命令均针对已编译或解压发布包中的 `WhiteboardExporter.exe`，成功退出码为 0，失败为 1。验证输出目录应与输入、已有导出资料分开，使用新的 `artifacts/acceptance/...` 子目录便于复核。

```text
WhiteboardExporter.exe --verify-import "输入清单.json" "验证输出目录"
WhiteboardExporter.exe --verify-library "已有导出目录" "验证输出目录"
WhiteboardExporter.exe --verify-cloud-metadata "验证输出目录"
WhiteboardExporter.exe --verify-cloud "白板ID清单.json" "验证输出目录"
```

| 入口 | 输入和行为 | 报告 |
| --- | --- | --- |
| `--verify-import` | 1–100 个本地文件绝对路径组成的 JSON 字符串数组；导入、重绘、导出全部七种格式并打开 PDF／图片预览。支持官方 ZIP、备份或 JSON 配套资源；缺少公开笔刷时可能联网恢复。 | `import-result.json` |
| `--verify-library` | 总目录、账号目录或单张白板目录；只读扫描并遍历原始重绘与已有 PDF／PNG／JPEG／SVG／HTML 预览，核对原文件哈希和修改时间不变，最多 500 张。输出目录必须在待检查目录之外。 | `library-result.json` |
| `--verify-cloud-metadata` | 使用本机导出器的当前登录会话，只读白板列表、当前账号和最后编辑时间，验证账号目录键的稳定性及清单往返；不会打开或采集白板。 | `cloud-metadata-result.json` |
| `--verify-cloud` | 1–100 个不同白板 UUID 组成的 JSON 字符串数组；使用当前登录会话逐张获取官方 ZIP、恢复资源、重绘、导出全部格式并校验 PDF／图片预览。 | `cloud-result.json` |

`--verify-import` 清单示例使用开发机自己的路径：

```json
[
  "C:\\PrivateWhiteboardTests\\board.zip",
  "C:\\PrivateWhiteboardTests\\backup.wbarchive"
]
```

云端入口需要账号持有人先在应用的微软网页登录界面完成登录、账号选择和多因素认证。账号会话不能从源码或发布包获取；微软要求重新交互时，CLI 会停止并记录失败，不能以旧的离线结果替代本次在线检查。

PowerShell 可等待隐藏的验收进程并取得退出码：

```powershell
$application = (Resolve-Path .\src\WhiteboardExporter.App\bin\Release\net10.0-windows\WhiteboardExporter.exe).Path
$process = Start-Process -FilePath $application -ArgumentList @('--verify-cloud-metadata', '".\artifacts\acceptance\cloud-metadata"') -WindowStyle Hidden -Wait -PassThru
$process.ExitCode
```

## 报告和私人资料边界

- `artifacts/`、输入清单、私人 ZIP／备份／JSON、导出文件、截图及浏览器配置留在本机，不提交 Git，不加入公开样本或发行包。公开样本应使用手工构造内容。
- `library-result.json` 记录统计、支持状态和固定错误类型；`cloud-metadata-result.json` 记录账号识别来源、结构诊断、计数及验证状态，不保存账号姓名／邮箱、实际账号键、白板标题／ID、日期值或认证正文。
- 元数据验证同时在输出目录创建 `verified-account-directory`，其中的 `account.json` 包含当前账号公开的姓名／邮箱和键。整个验证目录仍是私人资料，不能因统计报告没有身份值而公开目录。
- 导入及完整云端检查会生成真实白板文件；云端失败截图也可能显示账号界面。导入失败报告及通用初始化失败记录可能含本地路径或异常文字，应按私人诊断资料处理。
- 备份可保存白板源响应与资源；账号凭据、Cookie、复制的令牌及认证请求头不进入备份或日志。只读身份采集仅取当前用户的允许字段，不序列化完整组件或认证状态。

## 覆盖检查与结果判断

| 范围 | 应核对的行为 |
| --- | --- |
| 导入与安全 | 官方 ZIP、备份和 JSON 往返；原始字节保留；损坏 ZIP、资源篡改、路径穿越、别名冲突、压缩炸弹和链接目录可见失败；未知对象与缺图保留诊断。 |
| 绘制与资源 | 中文、负坐标、旋转、嵌套分组、图层顺序、SVG 笔迹、高亮、便签、表格和远端对象；公开纹理、普通图片及离线资源恢复。 |
| PDF／图片 | 动态纸张整板单页 PDF；A4／A3 和分页模式；100%／200% 图片、宽高各最多 20000、超限等比缩小；单文件 PNG／JPEG、透明 PNG 和白底 JPEG。 |
| 稳定导出 | 改变窗口、缩放和平移后仍导出整板；内部捕获接缝无空行、空列或位移；取消、目标锁定和长路径失败不提前覆盖旧文件。 |
| 预览与目录 | 同名白板 ID 与选择一致；同路径媒体覆盖后刷新；图片预览不锁文件；只读目录归并、按需加载、坏备份回退和仅媒体预览；HTML／SVG 移除活动内容并阻止远程资源。 |
| 重试与账号 | 取消续跑、失败重试、版本变化重采、哈希验证后复用、改名继续原目录；不同账号同 ID 隔离；未知账号不沿用旧名称；目录清单冲突保留备份归属；旧目录按上下文隔离。 |
| 编辑时间与身份 | 精确时间与账号只从允许来源读取；UTC 保存、来源和精度保留，采集时间单独记录；创建／查看日期、相对文字、无明确单位的数字不能冒充编辑时间；陈旧 React 分支不能冒充当前模型。 |

分别检查“格式是否生成”和“内容是否完整”。`complete` 表示当前解析和资源检查通过；`partial` 表示有明确缺失或不支持内容；`failed` 表示无法重绘但仍可保留源数据。`EMPTY_CANVAS` 只说明本次源包没有绘制对象，不能证明在线白板也为空。ZIP 结构有效、PDF 存在或截图可见均不能代替逐对象核对。

## 真实白板人工检查

使用自己的会话与资料执行下列步骤，报告仅保存在本机。

1. 准备含中文、不同笔刷、便签、普通图片、表格、旋转／嵌套分组和远端对象的白板。登录后读取列表，核对账号、白板 ID、编辑时间和去重结果，确认示例项已移除。
2. 用 `--verify-cloud` 或应用批量导出取得官方 ZIP，记录下载与每种格式状态。逐对象对照在线画布与本地重绘；图片占位、空源或缺图需要重试，先前原始 ZIP 应保留。
3. 将本次真实 ZIP 加入本地清单，独立运行 `--verify-import`。退出账号并断网，删除生成的 PDF／PNG，只留备份或 JSON／assets，再导入并重新生成 PDF、图片、SVG 和 HTML，核对资源完整。
4. 测试取消和失败重试、同一白板重复导出及改名。改变预览窗口和缩放后重新导出，核对完整边界、单页 PDF、尺寸、透明度与图片内容一致。
5. 使用两个真实账号分别导出并打开总目录，核对各自 `account.json`、任务状态、`board.account`、相同白板 ID 的隔离和账号切换。只读目录验证另运行 `--verify-library`。

验收记录至少包含：源码版本、架构、SDK／WebView2 版本、执行入口、通过／失败／待验收、内容支持状态、报告本机位置及未完成原因。私人白板名称、账号值、ID、原文和截图不得加入公开记录。组织账号策略和任意 Fluid 文件解析仍需单独评估；本软件不回写微软白板。
