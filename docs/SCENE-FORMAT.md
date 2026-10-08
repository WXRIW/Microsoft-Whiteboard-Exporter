# SceneDocument v1 数据格式

`scene.json` 是独立重绘的入口。所有坐标使用白板像素（CSS px），无需微软账号、原始页面或网络请求。JSON 为 UTF-8；目前只接受 `schemaVersion: 1`。不认识的字段应保留，以便后续版本使用。

## 顶层结构

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `schemaVersion` | 整数 | 固定为 `1` |
| `board` | 对象 | `{ "id": "…", "title": "…" }`，可附账号归属、源编辑时间与采集时间，ID 不含认证信息 |
| `bounds` | 对象 | `{x,y,width,height}`，整个画布的世界坐标边界，可包含负坐标 |
| `background` | 字符串 | CSS 颜色，通常为 `#ffffff` |
| `elements` | 数组 | 顶层元素，每个元素可包含子元素 |
| `assets` | 对象 | 以资源 ID 为键的资源目录 |
| `diagnostics` | 对象 | 解析结果、缺失资源和异常说明 |
| `unattachedComments` | 数组，可选 | 无法匹配画布定位的评论线程，避免正文丢失 |

`bounds.width/height` 非负。坐标与变换参数必须为有限数值，绝对值不超过 `100000000`。最多 100000 个元素，分组最多嵌套 32 层；这些限制用于防止损坏数据造成无限或极大画布。

## 白板日期与重复导出

`board.lastModifiedUtc` 为可选的源白板最后编辑时间，在线来源提供时保存为 UTC ISO 8601 字符串，例如 `2026-10-08T10:34:12.0000000+00:00`。`board.lastModifiedSource` 为可选的来源字段说明，例如 `microsoft-api:lastModifiedDateTime`。`board.capturedUtc` 为本次采集时间，与最后编辑时间分别保存；导入文件的创建／修改时间、备份清单 `createdUtc` 和导出任务完成时间均不作为源编辑时间。未知时间不填成当前时间。所有日期字段均可缺省；导入日期字段必须包含明确时区。

在线时间只读取表示最后编辑的字段：`lastModifiedTime`、`lastModifiedDateTime`、`modifiedTime`、`modifiedDateTime`、`lastEditedTime`、`lastEditedDateTime`。字段为 ISO 字符串时必须带明确时区；Unix 数值只有属性名明确以 `UnixSeconds` 或 `UnixMilliseconds` 结尾时才解析，不能根据位数猜单位。仅显示“昨天”“3 小时前”等相对说明时，时间保持未知。JSON 与 `.wbarchive` 中的场景保留相同日期；备份清单也保存已知日期及来源，离线重新导入或重新解析旧失败备份时继续保留。

列表卡片的已提交白板模型可提供精确 ISO 日期，仅读取 ID 与卡片相同的模型日期字段，来源例如 `microsoft-gallery:board-model:lastModifiedTime`。遍历有固定深度上限，不能采用未提交属性、其他白板、创建／查看时间或任意组件状态。

列表只有明确标注为 `Edited`／最后编辑的完整本地日期及时间时，按网页／浏览器区域格式确定年月日顺序，并使用该浏览器的时区转换为 UTC。`lastModifiedSource` 例如 `microsoft-gallery:edited-caption;locale=en-US;timezone=Asia/Shanghai;precision=minute`，表明日期仅精确到分钟，秒补零表示分钟起点；带秒的文字标记 `precision=second`。无效日期、只有日期没有时钟、夏令时缺失或重复的时钟均保持未知。文字日期不能当作精确版本号跳过云端采集，因此同一分钟内多次编辑仍会重新取源数据再比较。

重复导出的场景比较按 JSON 对象键排序，但不改变元素数组、绘制顺序或未知字段。仅采集时钟 `board.capturedUtc` 和 ZIP／清单创建时间变化不代表场景新版本；标题、最后编辑日期、诊断、实际图片和其他场景数据变化均保留。备份是否相同还要核对完整性清单与每个原始包、源响应及图片的实际字节，场景相同不能替代原始数据检查。JSON 是否相同必须核对全部相邻资源；损坏或缺失文件不能被视为已完成导出。

## 账号归属与目录索引

`board.account` 是可选的账号归属对象，不影响坐标与重绘。字段为：`key`（64 个十六进制字符组成的稳定目录键）、`displayName`（显示名称）、`kind`（`msa`、`aad`、`manual` 或 `local`），以及可选的 `email` 和 `source`。自动识别只读取微软当前账号公开的姓名、邮箱、类型及租户标识；`aad` 账号键区分租户。来源为白板页面当前已提交用户服务的自身数据属性：MSAL 的 `_userInfo` 或 Teams 的 `_userInfoCache`，`source` 分别为 `microsoft-whiteboard:header-userInfo` 或 `microsoft-whiteboard:header-userInfoCache`；名称中保留 `header` 是来源标识，不要求网页存在页头或账号弹窗。也可回退到可见当前账号 DOM 面板或 MeControl 当前账号配置，来源分别为 `microsoft-mecontrol:currentAccount-dom`、`microsoft-mecontrol:currentAccount`。当前 DOM 必须与已提交 React 树结构严格对应，不能沿复用节点的旧分支读取；不调用懒加载的用户信息 getter，不读取 `_account`、`idTokenClaims`、密码、Cookie、令牌或完整组件状态。无法确认身份时，用户提供独立账号名称，按名称生成 `manual` 键，不将多个未知账号放入同一个“未知账号”目录。没有账号信息的本地导入使用 `local` 的“本地导入”命名空间；重新导入带账号信息的备份则保留原归属。

新导出目录结构如下。账号目录标签使用经过文件名净化的邮箱或显示名称，后缀为账号键前 16 位；白板目录按标题和 ID 命名，同账号改名仍复用已有目录。

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
      assets/<sha256>.png
      source/<独立微软导出ZIP>
```

`account.json` 的结构为 `{ "schemaVersion": 1, "account": { ... } }`，其中 `account` 与 `board.account` 使用相同字段。任务记录存放于各账号目录自己的 `export-state.json`，白板任务另保存 `accountKey`。同一白板 ID 可以同时存在于不同账号目录；目录索引使用实际目录范围和白板标识共同建立条目身份，不把跨账号同 ID 的数据合并。账号键不用于认证，不包含 Cookie、令牌或认证请求头。

目录清单与已保存场景的账号冲突时，场景中的 `board.account` 保持权威，显示冲突诊断，不改写备份归属。旧独立账号目录没有清单或场景账号时，可根据父目录上下文和实际目录建立本地隔离命名空间，避免同 ID 在再次导出时覆盖；目录标签只是本地来源说明，不证明微软身份。单独导入无归属文件仍使用默认“本地导入”命名空间。在线列表刷新后无法识别当前账号时，清除先前的手工名称，用户需重新确认本次账号的独立名称。

未知或 `manual` 归属的云端条目，在当前账号已自动识别且该白板仍位于最新列表中时，可以绑定到 `msa`／`aad` 命名空间；不能把已确认属于其他微软账号的条目改绑。新命名空间不继承旧条目的媒体文件路径、源响应、图片缓存或已下载场景，需重新采集当前账号的数据；旧命名空间中的文件和备份不移动、不删除、不改写。此绑定仅适用于在线条目，不会在浏览旧本地备份时重写其账号字段。

“打开目录”兼容导出总目录、账号目录、单张白板目录及旧版平铺文件，不移动旧文件。扫描按目录和白板标识归并格式，先索引路径，选择条目后才读取完整场景；备份不能读取时可尝试 JSON 或官方 ZIP。仅有 PDF、PNG、JPEG、SVG 或自包含离线 HTML 的条目仍可只读预览，但媒体文件不能代替原始场景重新导出。HTML／SVG 通过离线净化器显示，移除活动内容并阻止远程资源，不执行原始网页脚本。

扫描排除源包缓存、`assets`、原始响应、浏览器配置和 `.history`，不将 `.previous-…` 的历史修订列成新白板；只关联当前输出。控制清单与任务 JSON 不作为 SceneDocument。扫描有数量及深度上限，可以取消，拒绝符号链接和目录联接。原始文件无法读取时保留诊断及其他可用候选，不通过联网补图来改变目录浏览结果。

## 导出尺寸与公开选项

`ExportOptions` 默认 `ImageScale = 1`、`PdfLayout = Canvas`。`ImageScale` 是百分比除以 100；默认 100% 按完整画板像素输出，1 CSS px 对应 1 输出像素。完整边界包含顶层 `bounds` 及所有变换后的对象，输出不取预览窗口或交互缩放。

桌面界面默认勾选离线 HTML 和 PNG 透明背景，可自行关闭。公开 `ExportOptions.Transparent` 的缺省仍为 `false`，调用者需要明确传入 `true` 才生成透明 PNG；JPEG 始终合成白底。界面选项不改写 `scene.background` 或原始数据。

设完整世界边界为宽 `W`、高 `H`，用户比例为 `s`，实际统一比例为 `min(s, 20000/W, 20000/H)`；输出宽、高分别向上取整并限制为最多 20000 像素。两个方向使用相同比例，小数边界的末尾可能留有不足 1 输出像素的空白。超限时自动等比缩小整张画板；例如 `9545 × 22007` 在 100% 时输出约 `8675 × 20000`。PNG 和 JPEG 各输出一个完整文件。

界面提供 13 档比例：10%、25%、50%、75%、100%、125%、150%、200%、300%、400%、500%、800%、1000%。公开接口也接受有限的正数比例。导出内部按小区域绘制并流式编码，以控制内存；没有公开分块选项或需要用户拼接的图片文件。

PDF 默认 `Canvas` 按整张画板比例生成动态纸张单页。普通画板的 1 CSS px 对应 0.75 PDF point；纸张尺寸向上对齐整 point，边缘可能有不足 1.34 CSS px 的留白。为兼容 PDF 阅读器，超过 200 英寸长边的纸张与内容统一等比缩小，继续保持完整单页和矢量几何；200 英寸是兼容策略，不是 WebView2 属性范围。A4/A3 适配和 A4 分页为可选排版。枚举固定值为 `A4Fit = 0`、`A3Fit = 1`、`A4Tiles = 2`、`Canvas = 3`，保留旧值以兼容已保存的配置。

图片和 PDF 的绘制区域均取自画板边界，不取预览窗口或交互缩放。源画板、`scene.json` 和资源数据不会因为输出尺寸选择发生修改。

## 元素

```json
{
  "id": "note-1",
  "kind": "note",
  "nativeType": "Note",
  "nativeId": "微软源对象的身份标识",
  "z": 4,
  "transform": [1, 0, 0, 1, -200, 120],
  "bounds": { "x": 0, "y": 0, "width": 304, "height": 305 },
  "style": { "opacity": 1 },
  "text": "中文便签",
  "html": "<div style='width:304px;height:305px;background:#fff1a8'>中文便签</div>"
}
```

- `id` 全场景唯一；`kind` 可为 `ink`、`highlighter`、`text`、`note`、`image`、`shape`、`connector`、`group`、`list`、`table`、`link`、`comment`、`unknown`。`nativeType` 保留微软原类型。`nativeId` 为可选字符串，来自原对象自身的 `data-identity-key`，没有时读取后代提供的标识；它用于同一白板的原生对象定位，不代替本软件 `id`，不保证分组与子项之间唯一。匿名 `element-N` 是解析时的场景 ID，不能单凭序号匹配另一次采集的图片。
- `transform` 为 SVG 六参数 `[a,b,c,d,e,f]`：`x' = a*x + c*y + e`，`y' = b*x + d*y + f`。分组的子元素在父元素坐标系中，使用父矩阵乘子矩阵。缺省时为单位矩阵。
- `bounds` 为元素变换前的局部边界。`z` 在同级元素中从小到大绘制，同值保持数组顺序。整体导出使用顶层 `bounds`，避免只截到预览窗口。
- `svg` 为 SVG 几何片段（可以包含一个内嵌 `<svg>`）；保留路径、压力变化后的笔迹轮廓、描边和箭头。`html` 为已经净化的可见内容，渲染器在局部边界处以 XHTML `foreignObject` 放置；`text` 保留提取的纯文字并用于无可见片段时的回退。
- `assetId` 引用 `assets`；HTML/SVG 中的图片可使用 `src="wbasset:资源ID"` 或 `href="wbasset:资源ID"`，并附 `data-wb-asset`。渲染器将其替换为本地资源编码。
- `children` 为相同结构的子元素数组。`style.opacity` 为整体透明度，范围 0–1。
- `sourceFragment` 保留未知类型的原始源片段，仅用于诊断和后续解析。渲染器不执行其中的脚本、事件或外链。
- 评论可包含 `commentThreadId` 和 `comments: [{author,body,displayDate}]`。

## 图片资源与诊断

```json
{
  "assets": {
    "asset-1": {
      "path": "assets/5d…a7.png",
      "mime": "image/png",
      "source": "images/original-picture.png"
    }
  },
  "diagnostics": {
    "status": "partial",
    "total": 6,
    "supported": 5,
    "unsupported": 1,
    "missingAssets": ["asset-2"],
    "items": [{"elementId":"unknown-1","severity":"warning","code":"UNSUPPORTED_TYPE","message":"保留未知对象源片段"}]
  }
}
```

持久化资源路径为 `assets/<完整SHA-256>.<扩展名>`，相对于 JSON 所在目录或备份包根目录；多个资源 ID 可共用相同文件。`source` 是原始相对路径或远程 URL，仅作来源说明。核心导入器不发出 HTTP 请求，缺失图片显示占位并进入诊断。

`sourceHash` 为可选的 64 位十六进制字符串：原始来源字符串的 UTF-8 字节经 SHA-256 计算所得，通常使用小写。在线采集完成后，私有、blob 或带签名参数的来源可以移除 `source`，保留 `sourceHash` 和实际资源字节。它用于后续重解析时关联已保存图片，不是资源内容哈希或可访问的下载地址。资源内容的哈希仍位于 `path` 文件名中。恢复会匹配来源哈希，不按资源 ID 或图片排列位置猜测；匹配到已脱敏资源后不会重新把签名网址写回场景元数据。

`dataUrl` 是导入和渲染之间的临时字段，保存时提取为资源文件并移除。自己的 JSON 可以用 `dataUrl` 提供图片，导入后也会规范化。支持 PNG、JPEG、GIF、WebP、SVG 和 BMP；SVG 在显示时净化。

`invalid: true` 为可选的本地验证结果，表示图片字节损坏、当前系统不支持解码或超出验证范围。原资源字节仍保留，重绘使用占位并附 `asset-invalid` 警告；只有得到可解码的实际资源后才清除标记。资源验证最多解码 1024 像素长边的缩略图，不修改备份的原始尺寸和字节。

`complete` 表示所有识别对象和图片可绘制且无未解决的解析警告；`partial` 表示有未知类型、缺图或其他解析警告；`failed` 表示没有可供重绘的白板画布。`complete` 不代表已与某个真实在线白板逐像素核对。

`EMPTY_CANVAS` 为信息级诊断，仅用于具有官方导出骨架、没有绘制对象的画布，允许 `complete` 和空 `elements`。它表示本次微软源导出为空，不能证明在线原画板没有内容。普通空 HTML、未识别的可见节点、截图及脚本不能通过这项识别。`IMAGE_NOT_READY` 表示源包只含图片加载骨架，保留占位并标记 `partial`。导入旧 `failed` 备份时，完整性检查后可从保留的源 ZIP／HTML 重新解析；成功重解析会附 `ARCHIVE_REPARSED_SOURCE` 信息诊断，保留原始字节及白板身份。

导入 `partial` 备份时，保留的源数据也可用于补充缺失 `nativeId`，附 `ARCHIVE_METADATA_REFRESHED` 信息诊断。已有可见对象、分组、图层、边界、资源字节和来源哈希保留；只有同 ID 与原类型的旧 `AzureImage`／`FluidImage` 未知对象可升级为图片，并同步该对象的当前诊断。匿名旧未知图片可以通过原 `sourceFragment` 的真实原生身份匹配，必须唯一，不能按序号猜测。只含加载骨架的对象升级后仍为 `partial`。完整 `complete` 场景不会被源数据重新解析覆盖；以上导入过程不改写输入备份。

## `.wbarchive` 备份包

备份包是 ZIP，包含：

```text
manifest.json
scene.json
assets/<sha256>.png
originals/source/microsoft-export.zip
originals/source/export.html
originals/source/comments.json
originals/<其他已获取的源响应>
```

`manifest.json` 包含 `format: "whiteboard-archive"`、`version: 1`、`sceneFile: "scene.json"`、创建时间、白板 ID、标题、状态，以及 `files` 对象。`files` 将每个文件路径映射到小写完整 SHA-256（清单本身不包含在哈希表中）。导入时检查缺失文件、未列出的额外文件和哈希不一致。

原始微软 ZIP 的字节不改写。认证 Cookie、令牌和认证请求头不属于备份内容。原生 `.whiteboard` 可以存于 `originals/source/native.whiteboard`，但会明确标记 `failed`，不会冒充已经成功重绘。

ZIP 与 JSON 导入拒绝绝对路径、`..` 路径穿越、Windows 保留名、大小写或分隔符别名冲突；相邻资源读取拒绝符号链接与目录联接。单个文件不超过 128 MiB，展开后总量不超过 512 MiB，最多 20000 个条目；异常压缩比也会被拒绝。

## 最小独立重绘示例

用任意浏览器打开 [minimal-redraw.html](minimal-redraw.html)，选择 `samples/demo-scene.json`，再选择 `samples` 目录以授权读取图片。示例只依赖浏览器本身，展示矩阵、分组、SVG、文字/HTML 及资源替换。完整兼容实现位于应用的 `Web` 目录。

自行实现时，应先验证结构，净化可见 SVG/HTML，阻止远程请求和脚本，再按矩阵与图层绘制；不能直接把导入源 HTML 当网页打开。

## 大图单文件编码与预览

PNG／JPEG 两轴上限均为 20000 像素，支持 20000 × 20000 的单张图片。绘制器内部顺序截取最多 4096 × 512 的区域，将像素写入整宽扫描行带，交给 Windows WIC 的无缓存编码器写盘；不创建完整的 400MP 位图。JPEG 仅在最终编码一次，并将透明像素合成到白底。取消或失败时清理临时输出，成功提交后才替换最终目标文件。

原生 PDF／WIC 写入使用长度小于 240 字符的短临时路径，避免将较长的账号目录和白板文件名直接交给原生接口。编码或打印完成后，由 .NET 流式复制到最终目录内的短名暂存文件；复制完成并检查取消状态后，才在同一目录原子替换目标。暂存文件与目标处于同一卷，因此跨卷的源临时目录不会使最终替换退化为先删除旧文件。取消、目标锁定或复制失败保留旧文件，并清理本次临时与暂存文件。

图片预览限制解码后的长边为 4096 像素，同时显示文件真实像素尺寸；该预览限制不会改变导出文件。独立原生编码及预览测试位于 `tests/WhiteboardExporter.ImageTests`。
