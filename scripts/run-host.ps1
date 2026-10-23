#requires -Version 7.2
# Own output until the host exits, and flush each console line for live readiness/log checks.
param(
    [Parameter(Mandatory)][string]$Dll,
    [Parameter(Mandatory)][string]$LogPath,
    [Parameter(Mandatory)][string]$ErrorLogPath
)
$ErrorActionPreference = 'Stop'
$writer = [IO.StreamWriter]::new($LogPath, $false, [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
try {
    & dotnet $Dll 2> $ErrorLogPath | ForEach-Object { $writer.WriteLine($_) }
    $hostExitCode = $LASTEXITCODE
} finally { $writer.Dispose() }
exit $hostExitCode
