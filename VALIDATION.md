# MultiMediaPlayer 验收记录

## 功能检查

- 布局测试执行 180 组横屏、竖屏、超宽和小窗口用例，共 7,331 条断言。验证了画面边界、不重叠、自动布局的有效内容面积、手动比例、插入、删除、交换和工作区校验。
- WPF 与原生播放器联调覆盖 4 路和 9 路视频、独立暂停与定位、声音切换、切换布局不重建播放器、移除与重新添加、保存后恢复进度及播放速度。
- 测试素材覆盖 JPEG、PNG、WebP、BMP、EXIF 方向、带旋转信息的 MP4、中文路径、坏图片、坏视频与缺失文件。带旋转信息的 MP4 从工作区恢复到 6 秒并保持暂停。
- 在 Windows 11 实窗检查视频和图片混排、选中视频、拖动分区、双击输入 `60:40`、拖动标题交换、旋转视频裁切铺满、`F11` 全屏及 `Esc` 退出。
- 将便携 ZIP 解压到含中文和空格的路径运行，检查进程模块，确认 .NET、LibVLC 和 SkiaSharp 都从解压目录加载。测试主机已有 .NET，独立的干净 Windows 11 环境待验证。
- 当前桌面只有一个活动显示器。实窗检查使用 1920×1080、100% 缩放。高缩放对应的小窗口布局纳入自动化检查，实际 150%、200% 系统缩放和跨屏移动待验证。

## 播放性能

实测主机为 Windows 11 x64（构建 26200）、Core i7-10870H、16 GB 内存，安装 Intel UHD 和 RTX 3050 Laptop GPU。使用发布包内的播放器、合成 H.264 30 fps 素材和默认硬件解码。两组 30 分钟测试同时运行，短测随后分别运行。

| 场景 | 时长 | CPU 平均 / 峰值 | 进程专用内存峰值 | 视频解码引擎峰值 | 采样记录到的丢帧 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 4 路 1080p30 | 30 分钟 | 1.7% / 4.9% | 665 MiB | 37% | 1 |
| 9 路 720p30 | 30 分钟 | 5.5% / 23.4% | 985 MiB | 41% | 4 |
| 9 路 1080p30 | 60 秒 | 5.9% / 8.8% | 1,197 MiB | 74% | 0 |
| 1 路 4K30 + 3 路 1080p30 | 60 秒 | 2.6% / 4.4% | 824 MiB | 73% | 0 |

四组测试均完成，未出现播放器报错。CPU 和内存按进程统计。视频解码引擎峰值来自 Windows GPU 计数器，代表采样时该进程占用的解码引擎百分比。长测原始报告位于 `artifacts/release/final-4x1080p30.json`、`artifacts/release/final-9x720p30.json`，短测报告以 `short-` 开头。

## 复现步骤

在工作区根目录使用 PowerShell 7。生成素材需要带 `libx264` 编码器的 FFmpeg。素材仅用于测试，发布包不附带素材。

```powershell
.\scripts\fixtures.ps1
.\scripts\publish.ps1
$player = (Resolve-Path 'artifacts\MultiMediaPlayer-win-x64\MultiMediaPlayer.exe').Path
Start-Process $player -WindowStyle Hidden -Wait -ArgumentList '--smoke --count 9 --media artifacts/fixtures/720p.mp4 --output artifacts/smoke-9.json'
Start-Process $player -WindowStyle Hidden -Wait -ArgumentList '--verify-workspace artifacts/fixtures/edge-cases.mmp.json --expected-errors 3 --output artifacts/edge-cases.json'
.\scripts\soak.ps1 -Executable $player -Media artifacts/fixtures/landscape.mp4 -Count 4 -Output artifacts/soak-4x1080p30.json
.\scripts\soak.ps1 -Executable $player -Media artifacts/fixtures/720p.mp4 -Count 9 -Output artifacts/soak-9x720p30.json
```

长测脚本默认运行 1800 秒，约每 5 秒采集进程 CPU、内存、原生播放计数和进度，约每 10 秒采集 GPU 引擎计数。`.json` 为完成报告，`.jsonl` 为逐次采样，`.gpu.jsonl` 为 GPU 数据。素材循环时 LibVLC 会重置计数，因此采样的显示帧和丢帧数可能漏掉循环边界的数据。
