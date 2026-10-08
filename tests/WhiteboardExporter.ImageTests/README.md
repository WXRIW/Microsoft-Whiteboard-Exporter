# Windows 原生图片编码测试

此独立控制台项目直接链接 `RasterImageWriter.cs`、`NativeExportFile.cs` 和测试用 `RasterComparison.cs`，不引用桌面应用，不使用私有白板数据，也不依赖额外 NuGet 包。需要本机 Windows 和 .NET 10；WPF 用于小图像素验证、接缝对比及单独的预览探测。

从仓库根目录运行：

```powershell
dotnet run --project tests/WhiteboardExporter.ImageTests -c Release -- artifacts/image-writer-tests
```

10 项测试覆盖 PNG 逐字节 BGRA／透明度／奇数宽／行顺序／96 DPI，JPEG 质量设置／透明白底／末尾短行带，未写全禁止提交，完成与释放后的生命周期，取消阶段不替换原目标，输入检查，超过 320 字符路径的真实 PNG／JPEG 原生写出与提交，取消或目标被锁定时保留旧文件并清理临时文件，以及真实 20000 × 20000 PNG、JPEG 单文件写盘。大图只读取 IHDR／SOF 尺寸，绝不完整解码，编码进程峰值工作集必须低于 512 MiB。原生编码先使用短系统临时路径，再通过 .NET 复制到目标目录的短名称暂存文件，最后在同一目录原子替换；暂存复制支持输出目录与系统临时目录位于不同磁盘。

可在两个独立新进程中验证与应用相同的预览解码路径：

```powershell
dotnet run --project tests/WhiteboardExporter.ImageTests -c Release -- --preview artifacts/image-writer-tests/synthetic-20000.png
dotnet run --project tests/WhiteboardExporter.ImageTests -c Release -- --preview artifacts/image-writer-tests/synthetic-20000.jpg
```

该模式读取原始尺寸，将预览长边限制为 4096，再实际取出缩小后的像素，检查峰值低于 512 MiB。这两个探测单独运行，不计入编码测试；不会修改源文件或缩小导出的 20000 × 20000 文件。

桌面冒烟生成公开的接缝样本后，可以单独验证与整图截屏的一致性：

```powershell
dotnet run --project tests/WhiteboardExporter.ImageTests -c Release -- --compare artifacts/smoke/seams.png artifacts/smoke/seams-reference.png
```

对比要求宽高与 alpha 完全相同；RGB 每通道差不超过 32，只有参考图 3×3 邻域颜色跨度至少 8 的边缘允许差超过 1，差异像素总数不超过 0.15%。这只用于不超过 8MP 的合成接缝样本，不用于接受生产文件或完整解码大图。该模式还在内存中构造并确认拒绝 1 像素空竖列、空横行、右侧区域横移、下方区域纵移四种错误，避免容差掩盖接缝缺口或位移。这一探测单独计数，不计入编码测试。

原生接口依据 [IWICBitmapFrameEncode](https://learn.microsoft.com/windows/win32/wic/-wic-imp-iwicbitmapframeencode) 和 [WICBitmapEncoderNoCache](https://learn.microsoft.com/windows/win32/api/wincodec/ne-wincodec-wicbitmapencodercacheoption)。
