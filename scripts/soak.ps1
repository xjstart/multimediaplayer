[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string] $Media,
    [string] $SecondaryMedia,
    [Parameter(Mandatory)][ValidateRange(1,9)][int] $Count,
    [ValidateRange(1,86400)][int] $Seconds = 1800,
    [Parameter(Mandatory)][string] $Output
)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $Executable).Path
$mediaFile = (Resolve-Path -LiteralPath $Media).Path
$report = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path -Parent $report) -Force | Out-Null
$gpuLog = "$report.gpu.jsonl"
[IO.File]::WriteAllText($gpuLog, '')
$arguments = "--soak --count $Count --seconds $Seconds --media `"$mediaFile`" --output `"$report`""
if ($SecondaryMedia) {
    $secondaryFile = (Resolve-Path -LiteralPath $SecondaryMedia).Path
    $arguments += " --secondary-media `"$secondaryFile`""
}
$proc = Start-Process -FilePath $app -ArgumentList $arguments -PassThru -WindowStyle Hidden
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    while (-not $proc.HasExited -and $watch.Elapsed.TotalSeconds -lt ($Seconds + 75)) {
        try {
            $gpu = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -Filter "Name LIKE 'pid_$($proc.Id)_%'" -ErrorAction Stop | Where-Object UtilizationPercentage -gt 0 | Select-Object Name,UtilizationPercentage)
            $sample = @{ elapsed = $watch.Elapsed.TotalSeconds; engines = $gpu }
        } catch { $sample = @{ elapsed = $watch.Elapsed.TotalSeconds; error = $_.Exception.Message } }
        Add-Content -LiteralPath $gpuLog -Value ($sample | ConvertTo-Json -Compress -Depth 4)
        Start-Sleep -Seconds 10
        $proc.Refresh()
    }
    if (-not $proc.HasExited) { throw "Playback diagnostics exceeded the timeout. See $report.jsonl." }
    if (-not (Test-Path -LiteralPath $report)) { throw "Playback diagnostics exited without a report (exit $($proc.ExitCode))." }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $result.checks
    if (-not $result.passed) { throw $result.error }
}
finally { if (-not $proc.HasExited) { Stop-Process -Id $proc.Id } }
