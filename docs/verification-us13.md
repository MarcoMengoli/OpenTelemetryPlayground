# US13 verification record

## Scope and revision

US13 extends the integrated baseline `622aa5f` (US12, including US10/US11).
Branch: `codex/us13-macos-readme`. The initial implementation commit is the commit
that first adds this record; subsequent execution results are recorded below and
in the linked PR with their exact SHA. No application service/contract or warehouse
JSON is changed. Inventory inspection is a read-only mode of the existing test executable.

This is **not a completed macOS runtime certification**. The relevant acceptance
criteria remain open until the real-service checks below run successfully on a Mac.

## Local environment observed, 2026-10-25

- macOS **26.6.2**, build **25G83**; `uname -m`: **arm64** (Apple Silicon).
- Installed .NET SDKs: 9.0.305 and 10.0.301, osx-arm64. Required SDK **10.0.400 absent**.
- `pwsh` and `docker`: absent from PATH and the standard installation locations checked;
  no Docker.app in `/Applications`.
- No system software installed. Installation authorization was requested separately;
  missing prerequisites are not bypassed by weakening `global.json` or skipping checks.
- Intel hardware: **not tested locally**. Windows/Linux runtime: **not tested locally**.
- Dedicated worktree: `/Users/marcomengoli/Documents/ChatGPT/Otel/us13-macos-readme`.
  Reserved environment: `us13-macos`, base 18800; no environment/container was started.

All commands in the following table ran in **zsh, from that worktree root**, except
host inspection (which does not depend on working directory):

| Command | Observed result |
| --- | --- |
| `sw_vers`; `uname -m`; `dotnet --info` | Environment above recorded |
| `command -v pwsh dotnet docker gh` | Only dotnet and gh found |
| `dotnet restore --locked-mode` | **Failed**, exit 155: compatible SDK not found; requested 10.0.400 |
| `python3 -B -m unittest discover -s .github/tests -v` | **6 passed**, 0 skipped |
| `python3 .github/scripts/check_architecture.py` | Passed source/manifest guard; **not** a restored-dependency check |
| `git diff --check` | Passed |

Not executed locally: build, .NET tests, PowerShell parser/process tests, locked
restore dependency guard, container architecture inspection/pull, all real broker
and service scenarios. No test counts are inferred from an older commit.

## Portability checks added

`pwsh -NoProfile -File ./scripts/test-platform.ps1` (zsh or PowerShell, repository
root) exercises actual child processes without Docker or .NET SDK:

- launcher exits before the child is inspected; literal paths include spaces and
  an apostrophe, arguments include spaces and shell metacharacters;
- identity comparison from another PowerShell process; rejected stale identity
  and mismatched executable; graceful stop of the test-owned child;
- refusal to reset a foreign owner and refusal of a linked run directory on Unix.

The CI `portability` matrix runs this plus locked restore, Release build and offline
.NET tests on Ubuntu, Windows, macOS arm64 and macOS Intel. These are **offline**
checks. Windows symlink creation is not claimed by the Unix-only link test. CI
results must be read for the actual PR SHA; adding a job is not a passing result.
The existing Ubuntu job retains the real Docker/broker/E2E/recovery walkthroughs.

## Remaining real macOS acceptance checks

Run only after prerequisites are available. Record `git rev-parse HEAD`, OS/build,
chip, PowerShell version, `dotnet --info`, Docker/Compose version and engine/image
architecture alongside exact commands and output. Keep Apple Silicon and Intel
results separate; do not label emulated amd64 as native arm64.

**Shell: zsh. Working directory: repository root. These are pending commands, not results.**

```zsh
git rev-parse HEAD
sw_vers
uname -m
pwsh -NoProfile -Command '$PSVersionTable'
dotnet --info
docker compose version
docker info --format '{{.OSType}}/{{.Architecture}}'
docker buildx imagetools inspect rabbitmq:4.1.4-management
docker pull rabbitmq:4.1.4-management
docker image inspect rabbitmq:4.1.4-management --format '{{.Os}}/{{.Architecture}}'
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
pwsh -NoProfile -File ./scripts/test-platform.ps1
pwsh -NoProfile -File ./scripts/lab.ps1 start -Name us13-macos -PortBase 18800
pwsh -NoProfile -File ./scripts/lab.ps1 ready -Name us13-macos -PortBase 18800
pwsh -NoProfile -File ./scripts/lab.ps1 verify -Name us13-macos -PortBase 18800
pwsh -NoProfile -File ./scripts/e2e.ps1 -Name us13-e2e -PortBase 18500 -NoBuild
```

Also execute the English [manual order/invoice/Inventory](getting-started.md#manual-order-to-invoice)
and [failure/recovery](getting-started.md#failure-and-recovery) blocks in PowerShell
from the repository root. The first leaves its generated lab running. Record the
OrderId/InvoiceId and verify they survive stop/start before explicitly resetting it.
Run from a fresh clone in a **path containing spaces**, using a distinct name/base.
A process-only path test does not replace this full checkout test.

For isolated reset, keep two named labs on disjoint ranges, complete an order in
both, reset only one, then confirm the other remains ready with the same invoice,
DB/files and broker volume. Test an occupied port, wrong base, foreign owner and
linked path: startup/stop/reset must refuse without touching the other lab. Exit
the launching terminal and use a new one for `ready`/`stop` to verify hangup behavior.
Retain logs/reports before cleanup. Do not run global prune or kills by process name.

Open acceptance criteria: real Mac fresh restore/build + eight hosts/broker;
order-to-invoice plus separate Inventory; decline; real failure/recovery;
stop/restart persistence; isolated reset; full checkout with spaces; native broker
and SQLite compatibility. Offline CI alone does not close these criteria.

## Sources checked for implementation and installation guidance

- [Microsoft .NET on macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos)
- [Microsoft PowerShell on macOS](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-macos)
- [Docker Desktop on Mac](https://docs.docker.com/desktop/setup/install/mac-install/)
- [PowerShell Start-Process, Unix detached processes](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process)
- [.NET macOS process start-time implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.OSX.cs)
- [Apple process identity fields](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h)
- [GitHub runner images](https://github.com/actions/runner-images)

Checked 2026-10-25. Vendor architecture availability is not evidence that this
application or the pinned broker image ran on that architecture.
