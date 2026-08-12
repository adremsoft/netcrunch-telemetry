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

**Not published to the PowerShell Gallery yet.** Import the module from a clone:

```powershell
Import-Module .\NetCrunch.Telemetry\NetCrunch.Telemetry.psd1
```

The path is relative to this folder. Once the module is published this becomes
`Install-Module NetCrunch.Telemetry`.

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
| `Set-NCTable` | Stage a table on the sensor page. |
| `Set-NCTimeSeries` | Stage a time chart. Timestamps are epoch milliseconds. |
| `Set-NCCategoryChart` | Stage a labelled bar chart. |
| `Get-NCTelemetryPayload` | Build the payload without sending. `-AsJson` to inspect it. |
| `Send-NCTelemetry` | Post everything as one request. Supports `-WhatIf`. |
| `Clear-NCTelemetry` | Empty the buffer without disconnecting. |
| `Disconnect-NCTelemetry` | Drop the endpoint and the buffer. |

`Get-Help <command> -Full` for details.

### Counters vs statuses

Counters are numbers you chart. **Statuses are what NetCrunch alerting acts on.** If something can be
wrong, express it as a status — a counter alone will not raise anything.

### Data objects

A table or chart rendered on the sensor's page, with no dashboard to configure:

```powershell
Set-NCTable -Id 'services' -Name 'Stopped Services' `
            -Columns 'Name', 'StartType' `
            -Rows @(, @('wuauserv', 'Manual'))

Set-NCCategoryChart -Id 'byOutcome' -Name 'Items by Outcome' -SeriesName 'Items' `
                    -Categories 'imported', 'skipped', 'failed' -Values 1204, 18, 3
```

`Id` is the object's identity across payloads — staging the same id again replaces it.

**Watch the comma on `-Rows`.** It is an array of arrays, and PowerShell unrolls a single nested
array, so one row needs `@(, @('a', 1))`. Several rows are fine as `@(@('a', 1), @('b', 2))`. The
module checks that each row is an array and says so if it is not.

Parallel arrays must match in length, and rows must match the column count. The receiver checks
neither and will render the mismatch, so the module rejects it. Arrays are also capped at 1024
entries, above which the receiver silently truncates — rejected locally for the same reason.

A data object's own `-Status` is part of what is *displayed*. **Alerting acts on statuses** — a red
table is not an alert, so send `Set-NCStatus` too if something should fire.

### Timestamps

The wire format has no timestamp type, and a raw clock value means nothing outside the process that
produced it. `Set-NCTimestamp` therefore emits two things: an age in seconds you can set a threshold
on, and a status message carrying the absolute time for a person to read.

```powershell
Set-NCTimestamp -Object 'Sync' -Counter 'Last Success Age s' -StatusKey 'Last Sync'
```

Age is computed when the payload is built, not when you call it.

## Authentication

Pass the sensor's token alongside the endpoint; it goes out as `Authorization: Bearer`:

```powershell
Connect-NCTelemetry -Endpoint $env:NC_TELEMETRY_URL -Token $env:NC_TELEMETRY_TOKEN
```

**The NetCrunch receiver does not verify the token yet.** Today the endpoint URL is itself the whole
credential: anyone who can reach the web server and knows the sensor name and node id can write to
that sensor. Sending a token now costs nothing and makes the script forward-compatible with the
receiver that enforces it. Until then treat **both** URL and token as secrets. See
[`spec/v1.md`](../spec/v1.md) §1.1.

The module never writes either to logs, verbose output or error messages; failures are re-raised with
the status code only. Pass them from environment variables or a credential store rather than
hard-coding them, and be aware that **PowerShell transcription captures whatever you type inline** —
which is a stronger reason to use the environment here than in the other languages.

## Tests

```powershell
.\tests\Invoke-ConformanceTests.ps1
```

Runs the shared fixtures in [`../conformance/cases`](../conformance/cases). No Pester dependency —
Windows PowerShell ships Pester 3.4 and PowerShell 7 ships Pester 5, whose syntaxes are incompatible,
and requiring a module install to verify a module is a poor trade for a runner this small.

## Editing this module

**Save `.ps1`, `.psm1` and `.psd1` files as UTF-8 *with* a BOM.**

Without one, Windows PowerShell 5.1 decodes the file as ANSI. A UTF-8 em dash then ends in byte
`0x94`, which CP1252 maps to a closing smart quote — and PowerShell treats smart quotes as string
delimiters, so the literal terminates early and the file fails to parse. In a comment it is
invisible; inside a string it breaks the module, and only on 5.1.

Run the conformance suite under both editions before committing. PowerShell 7 will not catch this.

## Known gaps

- **No lifetime-bound aggregates.** [`spec/client-model.md`](../spec/client-model.md) §2 defines
  `SelfCount`, `PartCount` and `CategoryCount`; this module implements none of them, and the
  conformance runner reports those cases as **skipped** rather than passed. They are worth much less
  here than elsewhere: the dominant PowerShell case is a script that collects, sends once and exits,
  where there is no long-lived object whose lifetime a count could be bound to.
- **No rate helper.** NetCrunch does not derive per-second values for telemetry counters, so a rate
  has to be computed and sent as its own counter. Fine for single-shot scripts; a long-running loop
  currently does that arithmetic itself.
- **The receiver does not enforce the token yet.** The client half is settled
  ([`spec/v1.md`](../spec/v1.md) §1.1); NetCrunch must issue tokens and verify the header before v1
  can be frozen. No script change is expected when that lands.
- **Not published to the PowerShell Gallery** while the module is alpha.
