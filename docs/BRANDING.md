# 应用标识

MCU StudioX 使用原创 SX 折带标识。哑黑与暖金构成主体，分层青蓝轮廓在背后形成火光。标识使用透明背景，没有芯片引脚或方形底板。图形不依赖字体或 emoji。

- `src/StudioX.Desktop/Assets/StudioXBrand.xaml`：唯一矢量母版。`StudioXMark` 与 `StudioXAppIcon` 共用 SX 折带，用于标题栏、欢迎页与 Windows 图标。
- `src/StudioX.Desktop/Assets/StudioX.ico`：16、20、24、32、40、48、64、128、256 像素，透明背景，32 位色。EXE、窗口和安装器均引用此文件。
- `src/StudioX.Desktop/Assets/StudioX.png`：256 像素预览图。
- 运行 `tools/New-AppIcon.ps1` 从母版重新生成 ICO / PNG；传入 `-PreviewDirectory .artifacts/branding` 可生成包含深浅背景和实际小尺寸的对照图。

界面直接使用 WPF 矢量，随 DPI 缩放；Windows 使用各尺寸单独渲染的图标。现有安装包不会被源码修改覆盖，下次生成安装包时自动使用新标识。
