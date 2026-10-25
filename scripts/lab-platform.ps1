# Shared by the laboratory and its offline process/portability checks.
#requires -Version 7.2
function Assert-LabPrerequisites([string]$Repo) {
    if (!$IsWindows -and !$IsLinux -and !$IsMacOS) { throw 'Supported platforms: Windows, Linux and macOS.' }
    foreach ($command in 'dotnet','pwsh','docker') {
        if (!(Get-Command $command -CommandType Application -ErrorAction SilentlyContinue)) {
            throw "Missing $command in PATH. See README prerequisites; laboratory NOT verified."
        }
    }
    $required = (Get-Content -LiteralPath (Join-Path $Repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actual = & dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0 -or "$actual".Trim() -ne $required) { throw "Install .NET SDK $required for this architecture (global.json); laboratory NOT verified."
    }
    $composeVersion = & docker compose version --short 2>&1
    if ($LASTEXITCODE -ne 0 -or "$composeVersion" -notmatch '^v?[2-9]\.') { throw 'Docker Compose v2 or later is required; run docker compose version.' }
    $engine = & docker info --format '{{.OSType}}' 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Docker engine unavailable. Start Docker Desktop (or your Linux engine), then run docker info.' }
    if ("$engine".Trim() -ne 'linux') { throw 'A Linux Docker engine is required; switch Docker to Linux containers.' }
}

function Get-ProcessIdentity($Process) {
    if ($IsLinux) {
        # Kernel start tick, independent of .NET's boot-time conversion.
        $stat = Get-Content -LiteralPath "/proc/$($Process.Id)/stat" -Raw
        $fields = $stat.Substring($stat.LastIndexOf(')') + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
        return "linux:$($fields[19])"
    }
    # On macOS .NET obtains the kernel process start timeval via proc_pidinfo;
    # retain its full precision, not the second-resolution output of ps/lstart.
    if ($IsMacOS) { return "macos:$($Process.StartTime.ToUniversalTime().Ticks)" }
    if ($IsWindows) { return "windows:$($Process.StartTime.ToUniversalTime().Ticks)" }
    throw 'Process identity unavailable on this platform; refusing to stop.'
}

function Assert-ProcessIdentity($Process, $Entry) {
    if ((Get-ProcessIdentity $Process) -cne $Entry.identity -or $Process.Path -cne $Entry.executable) {
        throw "PID ownership mismatch for $($Entry.service); refusing to stop it."
    }
}

function Start-LabProcess([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory,
    [string]$OutputPath, [string]$ErrorPath) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $WorkingDirectory
    if ($IsWindows) {
        $info.FileName = $Executable
    } else {
        # Fixed shell program; every path/argument is a separate argv entry, never
        # interpolated shell code. exec preserves the PID through sh/nohup/pwsh.
        # Detach from terminal hangup and use real file descriptors, not pipes
        # whose readers disappear when the launching pwsh session exits.
        $info.FileName = '/bin/sh'
        foreach ($arg in @('-c', 'out=$1; err=$2; shift 2; exec /usr/bin/nohup "$@" </dev/null >"$out" 2>"$err"',
            'playground-runner', $OutputPath, $ErrorPath, $Executable)) { $info.ArgumentList.Add($arg) }
    }
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    # Do not record the transient /bin/sh or nohup executable as the owner.
    $expected = (Get-Process -Id $PID).Path
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Runner exited during startup; inspect $ErrorPath." }
        if ($process.Path -ceq $expected) { return $process }
        Start-Sleep -Milliseconds 20
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Runner executable identity unavailable; inspect PID $($process.Id) and $ErrorPath."
}
