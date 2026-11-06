# MultiMediaPlayer · 多画面媒体播放器

Windows 11 x64 本地播放器。把视频和静态图片拖到同一个窗口，最多显示九个画面。自动适配按素材实际显示比例选择布局。默认完整显示，也可以单独选择居中裁切铺满。

## 使用

1. 解压 `MultiMediaPlayer-win-x64.zip`，运行目录内的 `MultiMediaPlayer.exe`。请保留解压目录中的其他文件。
2. 点击「添加媒体」、按 `Ctrl+O` 或拖入本地文件。新导入的视频默认循环播放，选中视频发声。选中图片或启用总静音时所有视频静音。
3. 点击画面切换控制目标。拖动标题到另一画面的上、下、左、右边缘可重新分区，拖到中央可交换位置。也可右键标题，选择目标画面及方向。拖动分隔线修改比例，双击分隔线可输入 `60:40` 这样的数值。点击「自动适配」可重新优化分区。
4. 在画面中按 `←` / `→` 快退或快进 5 秒，按住 `Shift` 跳转 30 秒。按住 `Ctrl` 滚动滚轮，或用底部按钮，以 25 个百分点为一档缩放画面。放大后按住左键拖动查看，点击「重置」恢复 100% 并居中。
5. 「主画面 + 辅助」将当前选中画面放进 60% 的主分区。`F11` 切换全屏，`Esc` 退出全屏。底部可调整速度、进度、显示模式和解码方式。
6. 用「保存工作区」记住媒体路径、布局、缩放、平移和进度。打开 `.mmp.json` 后视频保持暂停。媒体移动后可通过「重新定位」更新路径。

图片支持 JPEG、PNG、WebP、BMP。视频常用容器包括 MP4、MOV、MKV、AVI、WebM，实际编码支持由随包附带的 LibVLC 决定。首版按静态图展示图片。超过 8000 万像素的图片显示错误提示，显示缓存长边最多 4096 像素。

## 构建和验证

需要 Windows x64 和 .NET 10 SDK 10.0.403 或同系列更新补丁，发布脚本需要 PowerShell 7。构建无需预装 VLC。本工作区已在 `.tools/dotnet/` 放置 SDK，可将以下命令中的 `dotnet` 换成 `.\.tools\dotnet\dotnet.exe`，发布脚本会自动识别它。

```powershell
dotnet build MultiMediaPlayer.slnx -c Release
dotnet run --project tests/MultiMediaPlayer.Tests -c Release
pwsh -File scripts/publish.ps1
```

发布脚本生成 `artifacts/MultiMediaPlayer-win-x64.zip` 和对应目录，包含 .NET 桌面运行库、LibVLC 和 SkiaSharp。用于复现功能测试和性能测试的命令见 `VALIDATION.md`。

源码按纯布局与工作区模型、WPF 界面、媒体服务拆分。工作区使用版本为 1 的 JSON。日志保存在 `%LOCALAPPDATA%\MultiMediaPlayer\player.log`，只写本机。
