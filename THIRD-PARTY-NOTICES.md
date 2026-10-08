# 第三方依赖与参考

本项目是独立编写的导出器，并非微软官方应用。Microsoft、Whiteboard、Edge、WebView2 等名称归各自权利人所有。

## 运行依赖

- **Microsoft.Web.WebView2 1.0.3800.47**：由 Microsoft 发布，适用 [WebView2 SDK 许可条款](docs/licenses/WebView2-SDK-LICENSE.txt)，原文随源码与便携包提供。安装的 Microsoft Edge WebView2 Runtime 受其单独许可条款约束，不随本项目打包。
- **.NET / WPF 10.0.11**：.NET Foundation 与 Microsoft 的开源运行时和桌面框架。自包含发布附带这些运行时，相关通知位于 [运行时许可证](docs/licenses/DOTNET-RUNTIME-LICENSE.txt)、[运行时第三方通知](docs/licenses/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt) 和 [WPF 许可证](docs/licenses/WPF-LICENSE.txt)。更新 SDK/运行时版本后，发布者应从对应 NuGet runtime 包更新通知原文。
- **Windows Imaging Component（WIC）与 Shell 文件流**：图片导出调用 Windows 自带的 PNG／JPEG 编码器和 `SHCreateStreamOnFileEx`，按扫描行写入单个文件。相关系统 DLL 由 Windows 提供，不随便携包额外分发，也未增加图片编码 NuGet 包。接口依据微软 [WIC 编码文档](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-imp-iwicbitmapframeencode) 和 [文件流 API 文档](https://learn.microsoft.com/en-us/windows/win32/api/shlwapi/nf-shlwapi-shcreatestreamonfileex) 独立声明。

## 运行时白板资源

原始白板及其图片归各自权利人所有。恢复缺失笔刷时，软件仅按白板实际引用获取微软公开的彩虹／星空纹理，缓存于用户本机并纳入该白板备份；这些纹理不随源码、手工示例或便携软件包预装。它们的权利不受下面参考项目的 MIT 许可授予。账号内图片通过用户自己的微软会话读取，软件保存图片字节，不将登录 Cookie、授权请求头或复制的令牌纳入新增资源文件。

## 参考项目

实现参考了公开项目的工作流程和微软导出结构；未复制其程序代码或测试数据。

- [PanoramicData.MicrosoftWhiteboardExport](https://github.com/panoramicdata/PanoramicData.MicrosoftWhiteboardExport)：参考账号网页登录、白板列表及批量操作流程。下载完成、原始 ZIP 内容验证和状态判定由本项目独立实现。
- [whiteboard-bulk-export](https://github.com/wyh2001/whiteboard-bulk-export)：参考微软网页上的设置菜单、官方 ZIP 导出入口及列表操作方式。WebView2 下载等待、画布就绪检查、源包版本保留和资源恢复由本项目独立实现。
- [SQLBI-Whiteboard](https://github.com/sql-bi/SQLBI-Whiteboard)：参考微软导出 HTML 的 `data-whiteboard-type` / `canvasContent` 对象结构、评论 JSON、SVG 笔迹与常见类型说明。[导入说明](https://github.com/sql-bi/SQLBI-Whiteboard/blob/main/docs/microsoft-whiteboard-import.md)。

上述参考仓库目前声明为 MIT 许可。如将来直接纳入其代码，应同时纳入相应版本的版权声明与完整许可证。本仓库当前样例、JSON 格式、解析器、渲染器和应用代码为本次开发独立编写。
