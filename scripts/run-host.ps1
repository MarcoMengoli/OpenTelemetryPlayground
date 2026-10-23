#requires -Version 7.2
# Keep the output owner alive for the entire host lifetime. Redirecting via Start-Process
# in the short-lived lab.ps1 caller loses later console output when that caller exits.
param(
    [Parameter(Mandatory)][string]$Dll,
    [Parameter(Mandatory)][string]$LogPath,
    [Parameter(Mandatory)][string]$ErrorLogPath
)
$ErrorActionPreference = 'Stop'
& dotnet $Dll 1> $LogPath 2> $ErrorLogPath
exit $LASTEXITCODE
