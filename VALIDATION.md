# MultiMediaPlayer 验收记录

## 1.1.0 图片浏览与图片集（2026-11-07）

- Release 构建零警告、零错误；核心测试通过 7,370 条断言，包含自然排序、多选编辑、导航边界、深复制、版本 1/2 工作区兼容、相对路径和 2 MiB 大小限制。
- WPF 图片诊断通过 8 组检查：目录扫描与递归、受控解码并发、九画面混排、事务式图片集编辑、工作区恢复、目录增删与坏图恢复、位图释放、全屏与显式自动适配。报告为 `artifacts/image-navigation.json`。
- 受控解码检查每个 view 只运行一个解码任务，较早请求不能覆盖最新目标；取消应用和释放 view 后的结果不能提交。九画面诊断确认换图保持所有分区几何、view 标识及其他图片状态，不重建视频播放器。
- 连续切换 120 次后强制 GC，旧位图可回收，仅保留当前位图；报告记录进程专用内存采样。这是短测，未重跑历史 30 分钟长测。
- 音频集成回归 41 项通过，覆盖图片静音、三路／九路快速切换、暂停恢复、设备重绑、播放器替换、工作区恢复及关闭取消。
- 发布目录的图片诊断 8 组、四路及九路视频冒烟各 8 项均通过，报告分别为 `artifacts/image-navigation-published.json`、`artifacts/image-browse-smoke-4.json`、`artifacts/image-browse-smoke-9.json`。
- 在 1920×1080、100% 缩放实窗检查九画面导航、独立管理窗口、切回目录的草稿提示、取消不提交、全屏方向键换图；缩小至约 721×591，窄 view 自动隐藏序号，前后切换和管理按钮可见。
- 工作区新保存为版本 2，版本 1 继续可读。自定义集保存顺序及当前项；默认目录索引不写入文件。大小超限保存保留原文件。

复现图片诊断（会在 `artifacts/image-navigation-fixtures` 创建合成素材）：

```powershell
.\.tools\dotnet\dotnet.exe run --project tests/MultiMediaPlayer.Tests -c Release
.\.tools\dotnet\dotnet.exe build MultiMediaPlayer.slnx -c Release
$player = (Resolve-Path 'src/MultiMediaPlayer.App/bin/Release/net10.0-windows/win-x64/MultiMediaPlayer.exe').Path
Start-Process $player -WindowStyle Hidden -Wait -ArgumentList '--verify-image-navigation --output artifacts/image-navigation.json'
```

图片诊断默认使用 `artifacts/fixtures/720p.mp4`，可用 `--media` 指定其他本地视频。发布包为 `artifacts/MultiMediaPlayer-1.1.0-win-x64.zip`，生成命令为 `pwsh -File scripts/publish.ps1 -OutputDirectory artifacts/MultiMediaPlayer-1.1.0-win-x64`。

## 1.0.1 音频修复（2026-11-07）

- 本轮构建零警告、零错误；核心测试通过 7,346 条断言；音频集成测试 41 项通过，包含 35 次输出波形检查，实测非选中频率最小抑制为 88.37 dB。发布目录四路、九路 WPF 冒烟各通过 8 项检查，报告分别为 `artifacts/audio-fix-smoke-4.json`、`artifacts/audio-fix-smoke-9.json`。
- 固定 DirectSound 独立播放器缓冲区，禁用数字直通与跨播放器音量保存；初始输出为零音量。所有静音和音量写入统一读取最新意图，释放及重开媒体会使旧回调失效。
- 音频集成测试引用正式播放器代码，通过 NAudio 3.3.0 的 WASAPI 进程回环采集测试进程实际输出，排除其他应用声音。三路、九路各执行 100 次快速切换，并逐路验证声音归属；测量窗口约为切换后 200–450 毫秒，要求其他测试频率比目标频率至少低 30 dB。
- 覆盖启动期间切换、总静音、零音量、背景恢复后立即暂停、选中视频暂停和恢复、无音轨视频、图片、循环、删除及重新添加、播放器替换、工作区暂停恢复，以及关闭期间的待执行回调。
- 设备恢复自动化覆盖 300 毫秒通知合并、设备不可用及重新出现、重绑期间切换画面、关闭时取消恢复。正常重绑保留原生播放器句柄、视频进度和暂停状态。
- 通过禁用音轨注入音频停滞，验证两秒检查、每次设备变化至多一次重开恢复、持续失败单独报告音频错误，以及无设备时等待并保持视频前进。模拟设备事件不修改系统默认设备。
- 报告保存在 `artifacts/audio-tests.json`，包含实际频率测量值与明确的通过、失败或未验证状态。无默认输出设备、系统不支持回环或采集初始化失败时退出码为 2，不计为通过。NAudio 仅用于测试，不随播放器发布。
- 耳机物理插拔、真实系统默认设备切换及设备重新可用后五秒内恢复的实机验收仍待人工完成；自动化模拟事件不能替代这部分验证。

复现音频验收：

```powershell
pwsh -File scripts/audio-fixtures.ps1
.\.tools\dotnet\dotnet.exe run --project tests/MultiMediaPlayer.AudioTests -c Release
```

## 功能检查

- 布局测试执行 180 组横屏、竖屏、超宽和小窗口用例，共 7,346 条断言。验证了画面边界、不重叠、自动布局的有效内容面积、手动比例、插入、删除、交换和工作区校验。
- WPF 与原生播放器联调覆盖 4 路和 9 路视频、独立暂停与定位、声音切换、切换布局不重建播放器、移除与重新添加、保存后恢复进度及播放速度。
- 测试素材覆盖 JPEG、PNG、WebP、BMP、EXIF 方向、带旋转信息的 MP4、中文路径、坏图片、坏视频与缺失文件。带旋转信息的 MP4 从工作区恢复到 6 秒并保持暂停。
- 在 Windows 11 实窗检查视频和图片混排、选中视频、拖动分区、双击输入 `60:40`、拖动标题交换、旋转视频裁切铺满、`F11` 全屏及 `Esc` 退出。
- 将便携 ZIP 解压到含中文和空格的路径运行，检查进程模块，确认 .NET、LibVLC 和 SkiaSharp 都从解压目录加载。测试主机已有 .NET，独立的干净 Windows 11 环境待验证。
- 当前桌面只有一个活动显示器。实窗检查使用 1920×1080、100% 缩放。高缩放对应的小窗口布局纳入自动化检查，实际 150%、200% 系统缩放和跨屏移动待验证。

## 播放性能

以下长测为 1.0.0 的历史记录，1.0.1 音频修复未重新进行两组 30 分钟性能测试。

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
