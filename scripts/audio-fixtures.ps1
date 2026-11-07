[CmdletBinding()]
param([string] $Directory = 'artifacts/audio-fixtures')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
$frequencies = 440,617,797,997,1213,1451,1733,1999,2333
foreach ($frequency in $frequencies) {
    & ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'color=c=navy:size=160x90:rate=10' -f lavfi -i "sine=frequency=${frequency}:sample_rate=48000" -t 60 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -af volume=0.15 (Join-Path $Directory "$frequency.mp4")
    if ($LASTEXITCODE) { throw 'Audio fixture encoding failed.' }
}
& ffmpeg -hide_banner -loglevel error -y -i (Join-Path $Directory '2333.mp4') -t 1.5 -c copy (Join-Path $Directory 'loop.mp4')
if ($LASTEXITCODE) { throw 'Loop fixture encoding failed.' }
& ffmpeg -hide_banner -loglevel error -y -i (Join-Path $Directory '440.mp4') -an -c:v copy (Join-Path $Directory 'silent.mp4')
if ($LASTEXITCODE) { throw 'Silent fixture encoding failed.' }
& ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'color=c=teal:size=160x90' -frames:v 1 (Join-Path $Directory 'image.png')
if ($LASTEXITCODE) { throw 'Image fixture encoding failed.' }
