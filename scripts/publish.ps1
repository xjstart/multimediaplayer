[CmdletBinding()]
param([string] $Dotnet, [string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Dotnet) {
    $localSdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
}
$target = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo 'artifacts\MultiMediaPlayer-win-x64' }
$archive = "$target.zip"
Push-Location $repo
try {
    & $Dotnet publish 'src\MultiMediaPlayer.App\MultiMediaPlayer.App.csproj' -c Release -r win-x64 --self-contained true -o $target -p:PublishTrimmed=false -p:DebugType=None
    if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
    $licenses = Join-Path $target 'Licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item 'README.md' (Join-Path $target '使用说明.md') -Force
    if (Test-Path -LiteralPath 'VALIDATION.md') { Copy-Item 'VALIDATION.md' $target -Force }
    Copy-Item 'licenses\*' $licenses -Force
    $packageLine = & $Dotnet nuget locals global-packages --list | Select-Object -First 1
    $packageRoot = ($packageLine -split ': ', 2)[1]
    if (-not $packageRoot) { throw 'Could not find the NuGet package directory.' }
    $skia = Join-Path $packageRoot 'skiasharp.nativeassets.win32\4.154.0'
    Copy-Item (Join-Path $skia 'LICENSE.txt') (Join-Path $licenses 'SkiaSharp-LICENSE.txt') -Force
    Copy-Item (Join-Path $skia 'THIRD-PARTY-NOTICES.txt') (Join-Path $licenses 'SkiaSharp-THIRD-PARTY-NOTICES.txt') -Force
    $runtime = Get-Content (Join-Path $target 'MultiMediaPlayer.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $pack = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
        $source = Join-Path $packageRoot "$pack\$($framework.version)"
        foreach ($name in 'LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
            $file = Join-Path $source $name
            if (Test-Path -LiteralPath $file) { Copy-Item $file (Join-Path $licenses "$pack-$name") -Force }
        }
    }
    foreach ($relative in 'MultiMediaPlayer.exe', 'coreclr.dll', 'PresentationFramework.dll', 'libSkiaSharp.dll', 'libvlc\win-x64\libvlc.dll', 'libvlc\win-x64\plugins\audio_output\libdirectsound_plugin.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $target $relative))) { throw "Missing published dependency: $relative" }
    }
    & tar.exe -a -cf $archive -C (Split-Path -Parent $target) (Split-Path -Leaf $target)
    if ($LASTEXITCODE) { throw 'Could not create ZIP.' }
    Get-FileHash -Algorithm SHA256 -LiteralPath $archive
}
finally { Pop-Location }
