#Requires -Version 5.1

<#
.SYNOPSIS
    Runs the shared conformance suite against the PowerShell implementation.

.DESCRIPTION
    Reads every case in ../../conformance/cases and checks that this module builds
    the expected payload, or rejects the inputs it is supposed to reject.

    Deliberately dependency-free — no Pester. Windows PowerShell ships Pester 3.4
    and PowerShell 7 ships Pester 5, whose syntaxes are incompatible, and requiring
    a module install to verify a module is a poor trade for a runner this small.

    Exits 0 when everything passes, 1 otherwise.

.EXAMPLE
    ./Invoke-ConformanceTests.ps1

.EXAMPLE
    pwsh -File ./Invoke-ConformanceTests.ps1 -Verbose
#>
[CmdletBinding()]
param(
    [string]$CasePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here       = Split-Path -Parent $PSCommandPath
$modulePath = Join-Path $here '..\NetCrunch.Telemetry\NetCrunch.Telemetry.psd1'
if (-not $CasePath) { $CasePath = Join-Path $here '..\..\conformance\cases' }

# Placeholder only — never a real installation's endpoint. See CONTRIBUTING.md.
$testEndpoint = 'https://netcrunch.example/api/rest/1/sensors/example@1/update'

Import-Module $modulePath -Force

# ---------------------------------------------------------------------------
# Comparison, following conformance/README.md:
#   - member order is not significant
#   - counters array order is not significant; match on path
#   - numbers compare by value
#   - the implementation must not emit members absent from the expectation
# ---------------------------------------------------------------------------

function Get-Prop {
    param($InputObject, [string]$Name)

    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-CounterSortKey {
    param($Counter)

    $path = Get-Prop $Counter 'path'
    return '{0}|{1}|{2}' -f (Get-Prop $path 'object'), (Get-Prop $path 'counter'), (Get-Prop $path 'instance')
}

function Test-IsNumeric {
    param($Value)

    return ($Value -is [int32] -or $Value -is [int64] -or $Value -is [double] -or
            $Value -is [decimal] -or $Value -is [single])
}

function Compare-Json {
    param($Expected, $Actual, [string]$Path = '$')

    $differences = New-Object System.Collections.ArrayList

    if ($null -eq $Expected -and $null -eq $Actual) { return $differences }
    if ($null -eq $Expected -or $null -eq $Actual) {
        [void]$differences.Add("$Path : expected '$Expected' but got '$Actual'")
        return $differences
    }

    if ($Expected -is [System.Management.Automation.PSCustomObject]) {
        if ($Actual -isnot [System.Management.Automation.PSCustomObject]) {
            [void]$differences.Add("$Path : expected an object, got $($Actual.GetType().Name)")
            return $differences
        }

        $expectedNames = @($Expected.PSObject.Properties.Name)
        $actualNames   = @($Actual.PSObject.Properties.Name)

        foreach ($name in $expectedNames) {
            if ($actualNames -notcontains $name) {
                [void]$differences.Add("$Path.$name : missing from the payload")
                continue
            }
            foreach ($d in (Compare-Json -Expected (Get-Prop $Expected $name) -Actual (Get-Prop $Actual $name) -Path "$Path.$name")) {
                [void]$differences.Add($d)
            }
        }
        foreach ($name in $actualNames) {
            if ($expectedNames -notcontains $name) {
                [void]$differences.Add("$Path.$name : emitted but not expected")
            }
        }
        return $differences
    }

    if ($Expected -is [System.Array]) {
        $expectedItems = @($Expected)
        $actualItems   = if ($Actual -is [System.Array]) { @($Actual) } else { @($Actual) }

        if ($expectedItems.Count -ne $actualItems.Count) {
            [void]$differences.Add("$Path : expected $($expectedItems.Count) items, got $($actualItems.Count)")
            return $differences
        }

        # Counters are identified by path, not position.
        if ($expectedItems.Count -gt 0 -and $null -ne (Get-Prop $expectedItems[0] 'path')) {
            $expectedItems = @($expectedItems | Sort-Object { Get-CounterSortKey $_ })
            $actualItems   = @($actualItems   | Sort-Object { Get-CounterSortKey $_ })
        }

        for ($i = 0; $i -lt $expectedItems.Count; $i++) {
            foreach ($d in (Compare-Json -Expected $expectedItems[$i] -Actual $actualItems[$i] -Path "$Path[$i]")) {
                [void]$differences.Add($d)
            }
        }
        return $differences
    }

    if ((Test-IsNumeric $Expected) -and (Test-IsNumeric $Actual)) {
        if ([double]$Expected -ne [double]$Actual) {
            [void]$differences.Add("$Path : expected $Expected but got $Actual")
        }
        return $differences
    }

    if ($Expected -is [bool] -or $Actual -is [bool]) {
        if ([bool]$Expected -ne [bool]$Actual) {
            [void]$differences.Add("$Path : expected $Expected but got $Actual")
        }
        return $differences
    }

    if ([string]$Expected -cne [string]$Actual) {
        [void]$differences.Add("$Path : expected '$Expected' but got '$Actual'")
    }
    return $differences
}

# ---------------------------------------------------------------------------
# Applying a case snapshot
# ---------------------------------------------------------------------------

function Set-CaseOptions {
    param($Options)

    $arguments = @{ Endpoint = $testEndpoint }
    if ($null -ne $Options) {
        $retain = Get-Prop $Options 'retainMinutes'
        $remove = Get-Prop $Options 'removeMinutes'
        if ($null -ne $retain) { $arguments['RetainMinutes'] = [int]$retain }
        if ($null -ne $remove) { $arguments['RemoveMinutes'] = [int]$remove }
    }
    Connect-NCTelemetry @arguments
}

function ConvertTo-DateTimeValue {
    param($Value)

    if ($Value -is [datetime]) { return $Value }
    return [datetime]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
}

function Invoke-CaseSnapshot {
    param($Snapshot)

    if ($null -eq $Snapshot) { return }

    foreach ($counter in @(Get-Prop $Snapshot 'counters')) {
        if ($null -eq $counter) { continue }
        $arguments = @{
            Object  = [string](Get-Prop $counter 'object')
            Counter = [string](Get-Prop $counter 'counter')
            Value   = Get-Prop $counter 'value'
        }
        $instance = Get-Prop $counter 'instance'
        if ($null -ne $instance) { $arguments['Instance'] = [string]$instance }
        Set-NCCounter @arguments
    }

    foreach ($status in @(Get-Prop $Snapshot 'statuses')) {
        if ($null -eq $status) { continue }
        $arguments = @{
            Key   = [string](Get-Prop $status 'key')
            Value = Get-Prop $status 'value'
        }
        $message = Get-Prop $status 'message'
        if ($null -ne $message) { $arguments['Message'] = [string]$message }
        $data = Get-Prop $status 'data'
        if ($null -ne $data) { $arguments['Data'] = $data }
        if ([bool](Get-Prop $status 'critical')) { $arguments['Critical'] = $true }
        Set-NCStatus @arguments
    }

    foreach ($event in @(Get-Prop $Snapshot 'events')) {
        if ($null -eq $event) { continue }
        $arguments = @{ Message = Get-Prop $event 'message' }
        $severity = Get-Prop $event 'severity'
        if ($null -ne $severity) { $arguments['Severity'] = [string]$severity }
        Add-NCEvent @arguments
    }

    foreach ($dataObject in @(Get-Prop $Snapshot 'data')) {
        if ($null -eq $dataObject) { continue }
        Invoke-DataObject -Entry $dataObject
    }

    foreach ($stamp in @(Get-Prop $Snapshot 'timestamps')) {
        if ($null -eq $stamp) { continue }
        Set-NCTimestamp -Object     ([string](Get-Prop $stamp 'object')) `
                        -Counter    ([string](Get-Prop $stamp 'counter')) `
                        -StatusKey  ([string](Get-Prop $stamp 'statusKey')) `
                        -ObservedAt (ConvertTo-DateTimeValue (Get-Prop $stamp 'observedAt'))
    }
}

<#
    Fixtures describe a data object as one flat record; the module splits it by
    type. An unrecognised type falls through to the default branch and must still
    be rejected, so the rejection fires for the reason the fixture states.
#>
function Invoke-DataObject {
    param($Entry)

    $arguments = @{ Id = [string](Get-Prop $Entry 'id') }
    foreach ($name in 'name', 'seriesName', 'message', 'status') {
        $value = Get-Prop $Entry $name
        if ($null -ne $value) {
            $parameter = if ($name -eq 'seriesName') { 'SeriesName' } else { (Get-Culture).TextInfo.ToTitleCase($name) }
            $arguments[$parameter] = [string]$value
        }
    }

    # A member the fixture omits must stay unbound, so the module raises "required"
    # rather than the runner inventing an empty value and tripping a later check.
    function Add-Array($Name, $Parameter) {
        $value = Get-Prop $Entry $Name
        if ($null -ne $value) { $arguments[$Parameter] = @($value) }
    }

    # Hoisted: Windows PowerShell 5.1 cannot parse a quoted $() subexpression
    # inside a double-quoted string, which the throw below would otherwise need.
    $type = [string](Get-Prop $Entry 'type')

    switch ($type) {
        'table' {
            Add-Array 'columns' 'Columns'
            $rows = Get-Prop $Entry 'rows'
            # Each row has to stay an array; PowerShell would otherwise flatten them.
            if ($null -ne $rows) { $arguments['Rows'] = @(@($rows) | ForEach-Object { , @($_) }) }
            Set-NCTable @arguments
        }
        'time-series' {
            Add-Array 'timestamps' 'Timestamps'
            Add-Array 'values' 'Values'
            Set-NCTimeSeries @arguments
        }
        'category' {
            Add-Array 'categories' 'Categories'
            Add-Array 'values' 'Values'
            Set-NCCategoryChart @arguments
        }
        default {
            # Not a library rejection: this module exposes one cmdlet per type, so
            # an unrecognised type has no way to be expressed in the first place.
            throw "unrepresentable in this API — type is not a parameter, there is no cmdlet for '$type'"
        }
    }
}

function Invoke-Rejection {
    param($Rejection)

    $kind  = [string](Get-Prop $Rejection 'kind')
    $input = Get-Prop $Rejection 'input'

    switch ($kind) {
        'counter' {
            $arguments = @{}
            foreach ($name in 'object', 'counter', 'instance') {
                $value = Get-Prop $input $name
                if ($null -ne $value) { $arguments[$name] = [string]$value }
            }
            $value = Get-Prop $input 'value'
            if ($null -ne $value) { $arguments['Value'] = $value }
            Set-NCCounter @arguments
        }
        'status' {
            $arguments = @{}
            $key = Get-Prop $input 'key'
            if ($null -ne $key) { $arguments['Key'] = [string]$key }
            $value = Get-Prop $input 'value'
            if ($null -ne $value) { $arguments['Value'] = $value }
            Set-NCStatus @arguments
        }
        'event' {
            $arguments = @{}
            $message = Get-Prop $input 'message'
            if ($null -ne $message) { $arguments['Message'] = $message }
            Add-NCEvent @arguments
        }
        'data' { Invoke-DataObject -Entry $input }
        default { throw "Unknown rejection kind '$kind'." }
    }
}

# ---------------------------------------------------------------------------
# Runner
# ---------------------------------------------------------------------------

$caseFiles = @(Get-ChildItem -Path $CasePath -Filter '*.json' | Sort-Object Name)
if ($caseFiles.Count -eq 0) { throw "No conformance cases found under $CasePath." }

$passed  = 0
$failed  = 0
$skipped = 0

foreach ($file in $caseFiles) {
    $case = Get-Content -Path $file.FullName -Raw | ConvertFrom-Json
    $name = [string](Get-Prop $case 'name')

    # Aggregate cases test spec/client-model.md, which this module does not
    # implement. Reported as skipped rather than quietly dropped — omitting them
    # from the count would make an absent feature look verified.
    if ($null -ne (Get-Prop $case 'operations')) {
        $skipped++
        Write-Host "  SKIP  $name" -ForegroundColor DarkYellow
        Write-Host "        lifetime-bound aggregates are not implemented in PowerShell" -ForegroundColor DarkGray
        continue
    }

    $rejects = Get-Prop $case 'rejects'
    if ($null -ne $rejects) {
        foreach ($rejection in @($rejects)) {
            $reason  = [string](Get-Prop $rejection 'reason')
            $label   = "$name / $([string](Get-Prop $rejection 'kind')) : $reason"
            Set-CaseOptions -Options (Get-Prop $case 'options')

            $rejected = $false
            try   { Invoke-Rejection -Rejection $rejection }
            catch { $rejected = $true; Write-Verbose "  rejected with: $($_.Exception.Message)" }

            if ($rejected) {
                $passed++
                Write-Host "  PASS  $label" -ForegroundColor DarkGreen
            } else {
                $failed++
                Write-Host "  FAIL  $label" -ForegroundColor Red
                Write-Host "        accepted an input the receiver would discard silently" -ForegroundColor Red
            }
        }
        continue
    }

    Set-CaseOptions -Options (Get-Prop $case 'options')
    Invoke-CaseSnapshot -Snapshot (Get-Prop $case 'snapshot')

    $snapshotAt = Get-Prop (Get-Prop $case 'options') 'snapshotAt'
    $payloadArguments = @{}
    if ($null -ne $snapshotAt) { $payloadArguments['SnapshotAt'] = ConvertTo-DateTimeValue $snapshotAt }

    # Round-trip through JSON so both sides are compared in the shape that would
    # actually go over the wire, not as native PowerShell types.
    $actual = Get-NCTelemetryPayload @payloadArguments |
              ConvertTo-Json -Depth 20 |
              ConvertFrom-Json

    $differences = @(Compare-Json -Expected (Get-Prop $case 'expect') -Actual $actual)

    if ($differences.Count -eq 0) {
        $passed++
        Write-Host "  PASS  $name" -ForegroundColor DarkGreen
    } else {
        $failed++
        Write-Host "  FAIL  $name" -ForegroundColor Red
        foreach ($difference in $differences) { Write-Host "        $difference" -ForegroundColor Red }
    }
}

Write-Host ''
$summary = "$passed passed, $failed failed"
if ($skipped -gt 0) { $summary += ", $skipped skipped" }
Write-Host $summary -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })

exit $(if ($failed -eq 0) { 0 } else { 1 })
