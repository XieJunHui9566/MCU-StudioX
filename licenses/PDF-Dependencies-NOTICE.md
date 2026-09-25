# PDF 依赖说明

本版本的 PDF 文字读取和页面渲染分别使用以下 NuGet 包。随附原始许可文本与相关第三方通知。

| 组件 | 版本 | 用途 | 许可及来源 |
| --- | --- | --- | --- |
| PdfPig | 0.1.16 | 提取 PDF 文字 | `PdfPig-Apache-2.0.txt`、`PdfPig-NOTICES.txt`；[源码](https://github.com/UglyToad/PdfPig/tree/a7bb35662bbbf405efddad50aedc9bcdcf515afc) |
| PDFtoImage | 5.4.0 | PDF 页面渲染的 .NET 封装 | `PDFtoImage-MIT.txt`；[源码](https://github.com/sungaila/PDFtoImage/tree/5948628cb2cf7d070c7fd28b1b4496db39e3afcd) |
| bblanchon.PDFium.Win32 / PDFium | 152.0.7961 | Windows 原生 PDF 渲染 | `PDFium-LICENSE.txt`、`PDFium-Binaries-MIT.txt`；[PDFium](https://pdfium.googlesource.com/pdfium/)、[二进制包源码](https://github.com/bblanchon/pdfium-binaries/tree/c6529b58791d142002f819beb46e370e668797d7) |
| SkiaSharp / SkiaSharp.NativeAssets.Win32 | 4.150.1 | 页面图像编码 | `SkiaSharp-MIT.txt`、`SkiaSharp-THIRD-PARTY-NOTICES.txt`、`Skia-BSD-3-Clause.txt`；[SkiaSharp 源码](https://github.com/mono/SkiaSharp/tree/c3e4f4c20e1f23ab74d31a8838a5bd6dc55365f2)、[Skia 源码](https://skia.googlesource.com/skia/) |

包版本和依赖关系以 `Directory.Packages.props` 与 NuGet 包元数据为准。`PDFium-LICENSE.txt` 和 `Skia-BSD-3-Clause.txt` 取自其上游项目公开许可文件；其余文本取自对应包或固定源码提交。
