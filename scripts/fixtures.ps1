[CmdletBinding()]
param([string] $Directory = 'artifacts/fixtures')
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Path $target -Force | Out-Null
function Encode([string[]] $Arguments) {
    & ffmpeg -hide_banner -loglevel error -y @Arguments
    if ($LASTEXITCODE) { throw "FFmpeg failed with exit code $LASTEXITCODE." }
}
Push-Location $target
try {
    Encode @('-f','lavfi','-i','testsrc2=size=1920x1080:rate=30','-f','lavfi','-i','sine=frequency=440:sample_rate=44100','-t','40','-c:v','libx264','-preset','veryfast','-crf','24','-pix_fmt','yuv420p','-c:a','aac','-af','volume=0.03','landscape.mp4')
    Encode @('-i','landscape.mp4','-vf','scale=1280:720','-c:v','libx264','-preset','veryfast','-crf','24','-c:a','copy','720p.mp4')
    Encode @('-display_rotation','90','-i','720p.mp4','-map','0','-c','copy','旋转竖屏.mp4')
    Encode @('-f','lavfi','-i','testsrc2=size=3840x2160:rate=30','-t','12','-c:v','libx264','-preset','ultrafast','-crf','24','-pix_fmt','yuv420p','4k.mp4')
    Encode @('-f','lavfi','-i','testsrc2=size=720x1280:rate=1','-frames:v','1','竖屏.png')
    Encode @('-f','lavfi','-i','smptebars=size=750x750:rate=1','-frames:v','1','方图.webp')
    Encode @('-f','lavfi','-i','testsrc2=size=1280x400:rate=1','-frames:v','1','宽图.bmp')
    Encode @('-f','lavfi','-i','smptebars=size=1200x800:rate=1','-frames:v','1','horizontal.jpg')
    $jpeg = [IO.File]::ReadAllBytes((Join-Path $target 'horizontal.jpg'))
    # JPEG APP1 containing a little-endian EXIF orientation tag set to 6.
    [byte[]] $app1 = 255,225,0,34,69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0
    [IO.File]::WriteAllBytes((Join-Path $target 'exif-orientation-6.jpg'), [byte[]]($jpeg[0..1] + $app1 + $jpeg[2..($jpeg.Length-1)]))
    Set-Content -LiteralPath '坏视频.mp4' -Value 'invalid fixture'
    Set-Content -LiteralPath '坏图片.png' -Value 'invalid fixture'
    $items = foreach ($file in '旋转竖屏.mp4','exif-orientation-6.jpg','坏视频.mp4','坏图片.png','不存在.png') {
        @{ Id = [guid]::NewGuid(); Path = $file; Kind = if ($file.EndsWith('.mp4')) {'Video'} else {'Image'};
           PositionMs = if ($file.StartsWith('旋转')) {6000} else {0}; Fit = 'Cover' }
    }
    @{ Version=1; Items=@($items); Mode='Automatic'; Muted=$true } | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 'edge-cases.mmp.json'
}
finally { Pop-Location }
