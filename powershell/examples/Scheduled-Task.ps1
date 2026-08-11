<#
.SYNOPSIS
    Reporting the outcome of a scheduled task to NetCrunch.

.DESCRIPTION
    The shape worth copying is the try/catch/finally: staging happens in both the
    success and the failure path, and the send happens in finally so a crash still
    reports something.

    What it does NOT need is a "job did not run" branch. If the script never
    executes at all, nothing arrives, the status expires after RetainMinutes, and
    NetCrunch raises the alert by itself. That case is covered by not being covered.

    Create a Telemetry sensor on the node representing this machine, and put the URL
    it shows into NC_TELEMETRY_URL — as a machine environment variable, or from a
    credential store. The URL is effectively a credential; keep it out of the script.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Scheduled-Task.ps1
#>
[CmdletBinding()]
param(
    [string]$Endpoint = $env:NC_TELEMETRY_URL
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module NetCrunch.Telemetry

if ([string]::IsNullOrWhiteSpace($Endpoint)) {
    throw 'NC_TELEMETRY_URL is not set. Copy the URL from the Telemetry sensor form.'
}

# 1500 minutes = 25 hours: a nightly job gets an hour of grace before the status
# expires and NetCrunch alerts.
Connect-NCTelemetry -Endpoint $Endpoint -RetainMinutes 1500

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

try {
    $processed = 0
    $failed    = 0

    foreach ($item in Get-WorkItems) {
        try {
            Invoke-WorkItem -Item $item
            $processed++
        }
        catch {
            $failed++
            # An event per failure: discrete things that happened, not a state.
            Add-NCEvent -Message "Item $($item.Name) failed: $($_.Exception.Message)" -Severity error
        }
    }

    $stopwatch.Stop()

    Set-NCCounter -Object 'Nightly Job' -Counter 'Items Processed' -Value $processed
    Set-NCCounter -Object 'Nightly Job' -Counter 'Items Failed'    -Value $failed
    Set-NCCounter -Object 'Nightly Job' -Counter 'Duration s'      -Value ([int]$stopwatch.Elapsed.TotalSeconds)

    # Only a status will raise an alert. Counters alone will not.
    if ($failed -gt 0) {
        Set-NCStatus -Key 'Nightly Job' -Value 'Warning' `
                     -Message "$processed processed, $failed failed" `
                     -Data @{ processed = $processed; failed = $failed }
    }
    else {
        Set-NCStatus -Key 'Nightly Job' -Value 'OK' `
                     -Message "$processed items in $([int]$stopwatch.Elapsed.TotalSeconds)s"

        # Age of the last clean run — alert on this if the job starts silently
        # succeeding at nothing.
        Set-NCTimestamp -Object 'Nightly Job' -Counter 'Last Success Age s' -StatusKey 'Last Clean Run'
    }
}
catch {
    # The job itself fell over. Report it rather than dying silently.
    Set-NCStatus -Key 'Nightly Job' -Value 'Error' -Message $_.Exception.Message -Critical
    throw
}
finally {
    # Runs on both paths, including the rethrow above.
    Send-NCTelemetry
}
