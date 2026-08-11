# netcrunch-telemetry — PowerShell

**Status: not started.** Intended as the first implementation.

## Why first

NetCrunch installations are Windows-centric and full of scheduled tasks, maintenance scripts and
batch jobs that nothing currently watches. A script that reports its own outcome is the smallest
possible useful case, needs no build toolchain, and no competing monitoring product offers it.

It is also the clearest demonstration of the push model's real advantage: a job that runs for two
minutes at 3am cannot be polled, but it can report.

## Planned surface

```powershell
Import-Module NetCrunch.Telemetry
Connect-NCTelemetry -Endpoint $env:NC_TELEMETRY_URL

Send-NCStatus  -Key 'Nightly Backup' -Value 'OK' -Message "$count files, $duration"
Send-NCCounter -Object 'Backup' -Counter 'Files Copied' -Value $count
Send-NCEvent   -Message 'Nightly backup completed'
```

## Shape

Unlike the long-running-process implementations, a script is usually a **single flush**: collect,
send once, exit. The module should make that the default path — no background timer, no explicit
shutdown — while still supporting a periodic flush for scripts that loop.

The dead-man's-switch case matters most here. A job that fails to run at all sends nothing, the
status expires after `retain` minutes, and NetCrunch raises the alert. That behaviour is free and
should be documented prominently, because it is the reason to instrument a scheduled task in the
first place.
