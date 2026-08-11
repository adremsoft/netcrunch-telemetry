# netcrunch-telemetry — PowerShell

**Status: alpha.** Implements the v1 subset of [`spec/v1.md`](../spec/v1.md) — counters, statuses
and events. Passes the shared conformance suite on Windows PowerShell 5.1 and PowerShell 7.

## Why this is the first implementation

NetCrunch installations are Windows-centric and full of scheduled tasks, maintenance scripts and
batch jobs that nothing currently watches. A script that reports its own outcome is the smallest
useful case, needs no build toolchain, and no competing monitoring product offers it.

It is also the clearest demonstration of what push buys you: a job that runs for four minutes at 3am
cannot be polled, but it can report.

## Install

```powershell
Import-Module .\NetCrunch.Telemetry\NetCrunch.Telemetry.psd1
```

Requires Windows PowerShell 5.1 or PowerShell 7+. No dependencies.

## Use

```powershell
Import-Module NetCrunch.Telemetry
Connect-NCTelemetry -Endpoint $env:NC_TELEMETRY_URL -RetainMinutes 90

try {
    $files = Copy-Backup
    Set-NCCounter -Object 'Backup' -Counter 'Files Copied' -Value $files
    Set-NCStatus  -Key 'Nightly Backup' -Value 'OK' -Message "$files files"
    Add-NCEvent   -Message 'Nightly backup completed'
}
catch {
    Set-NCStatus -Key 'Nightly Backup' -Value 'Error' -Message $_.Exception.Message -Critical
}
finally {
    Send-NCTelemetry
}
```

See [`examples/Scheduled-Task.ps1`](examples/Scheduled-Task.ps1) for the full pattern.

## Staging, then sending

Values are **buffered** and go out as one payload when you call `Send-NCTelemetry`. That is not an
optimisation — the receiver caps pending payloads per sensor and drops the overflow *silently*, so a
script that posted once per value would lose data without being told.

Because a payload carries absolute current values rather than deltas, sending is idempotent. A retry
after a timeout cannot double-count, which is why transport failures and 5xx responses are retried
automatically. 4xx responses are not; repeating a rejected request will not change the answer.

Events are cleared once sent. Counters and statuses are kept, so a script that loops keeps reporting
current values without restating them.

## The dead man's switch

This is the reason to instrument a scheduled task, and it needs no code.

`RetainMinutes` tells NetCrunch how long values stay live after arriving. Set it longer than the job's
interval. If the job runs and reports, the status refreshes. **If the job never runs at all** — the
scheduler was disabled, the server was down, the script died before `finally` — nothing arrives, the
status expires, and NetCrunch alerts on its own.

A polling monitor cannot see this. It has nothing to poll.

For a nightly job, `-RetainMinutes 1500` (25 hours) gives a one-hour grace period before the alert.

## Commands

| Command | Purpose |
| --- | --- |
| `Connect-NCTelemetry` | Set the endpoint and options; start a fresh buffer. |
| `Set-NCCounter` | Stage a number. Re-setting the same counter replaces its value. |
| `Set-NCStatus` | Stage a state with an optional message. **This is what alerts fire on.** |
| `Add-NCEvent` | Stage a discrete occurrence. Accumulates. |
| `Set-NCTimestamp` | Record when something last happened — see below. |
| `Get-NCTelemetryPayload` | Build the payload without sending. `-AsJson` to inspect it. |
| `Send-NCTelemetry` | Post everything as one request. Supports `-WhatIf`. |
| `Clear-NCTelemetry` | Empty the buffer without disconnecting. |
| `Disconnect-NCTelemetry` | Drop the endpoint and the buffer. |

`Get-Help <command> -Full` for details.

### Counters vs statuses

Counters are numbers you chart. **Statuses are what NetCrunch alerting acts on.** If something can be
wrong, express it as a status — a counter alone will not raise anything.

### Timestamps

The wire format has no timestamp type, and a raw clock value means nothing outside the process that
produced it. `Set-NCTimestamp` therefore emits two things: an age in seconds you can set a threshold
on, and a status message carrying the absolute time for a person to read.

```powershell
Set-NCTimestamp -Object 'Sync' -Counter 'Last Success Age s' -StatusKey 'Last Sync'
```

Age is computed when the payload is built, not when you call it.

## Keep the endpoint secret

The endpoint URL currently carries the sensor identity and is effectively the credential — see
[`spec/v1.md`](../spec/v1.md) §1, where this is flagged as unresolved before v1 can be frozen.

The module never writes the URL to logs, verbose output or error messages; failures are re-raised
with the status code only. Pass it from an environment variable or a credential store rather than
hard-coding it, and be aware that PowerShell transcription will capture it if you type it inline.

## Tests

```powershell
.\tests\Invoke-ConformanceTests.ps1
```

Runs the shared fixtures in [`../conformance/cases`](../conformance/cases). No Pester dependency —
Windows PowerShell ships Pester 3.4 and PowerShell 7 ships Pester 5, whose syntaxes are incompatible,
and requiring a module install to verify a module is a poor trade for a runner this small.

## Known gaps

- **No rate helper.** NetCrunch does not derive per-second values for telemetry counters, so a rate
  has to be computed and sent as its own counter. Fine for single-shot scripts; a long-running loop
  currently does that arithmetic itself.
- **No `data` objects.** Tables, time-series and category charts are deferred from v1 pending a
  decision on client ergonomics.
- **No authentication beyond the endpoint URL.** Blocked on the spec.
- **Not published to the PowerShell Gallery** while the module is alpha.
