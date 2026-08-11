#Requires -Version 5.1

<#
    NetCrunch.Telemetry

    Push metrics, states and events from a PowerShell script into NetCrunch.

    The module buffers values and sends them as a single payload when you call
    Send-NCTelemetry. That is deliberate: the receiver caps pending payloads per
    sensor and drops the overflow silently, so one request carrying everything is
    both cheaper and safer than one request per value.

    Because a payload carries absolute current values rather than deltas, delivery
    is idempotent — a retried or duplicated send cannot corrupt the result.

    See ../../spec/v1.md for the wire format this implements.
#>

Set-StrictMode -Version Latest

$script:MaxStatusKeyLength = 500
$script:MaxDataEntries     = 1024
$script:JsonDepth          = 20
$script:State              = $null

# ---------------------------------------------------------------------------
# Private helpers
# ---------------------------------------------------------------------------

function Get-NCProperty {
    param($InputObject, [string]$Name)

    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [System.Collections.IDictionary]) {
        if ($InputObject.Contains($Name)) { return $InputObject[$Name] }
        return $null
    }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-NCConnected {
    if ($null -eq $script:State) {
        throw 'Not connected. Call Connect-NCTelemetry first.'
    }
}

function Test-NCNumeric {
    param($Value)

    return ($Value -is [byte]    -or $Value -is [sbyte]   -or
            $Value -is [int16]   -or $Value -is [uint16]  -or
            $Value -is [int32]   -or $Value -is [uint32]  -or
            $Value -is [int64]   -or $Value -is [uint64]  -or
            $Value -is [single]  -or $Value -is [double]  -or
            $Value -is [decimal])
}

function New-NCCounterKey {
    param([string]$Object, [string]$Counter, [string]$Instance)

    $nul = [char]0
    return $Object + $nul + $Counter + $nul + $Instance
}

function Format-NCTimestamp {
    param([datetime]$Value)

    return $Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'",
        [System.Globalization.CultureInfo]::InvariantCulture)
}

<#
    Strips the endpoint out of an error before it reaches the user.

    The endpoint URL currently carries the sensor identity and is effectively the
    credential — spec/v1.md section 1. Invoke-RestMethod puts the full URL in its
    exception messages, so anything surfaced from a failed send has to be rebuilt
    rather than passed through, or the credential lands in transcripts and CI logs.
#>
function Get-NCSafeErrorMessage {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord, [int]$StatusCode)

    if ($StatusCode -gt 0) {
        return "NetCrunch telemetry send failed with HTTP $StatusCode."
    }

    $type = $ErrorRecord.Exception.GetType().Name
    return "NetCrunch telemetry send failed ($type). The endpoint was unreachable or the request timed out."
}

function Get-NCStatusCode {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)

    # Windows PowerShell raises WebException; PowerShell 7 raises
    # HttpResponseException. Both expose Response.StatusCode, but neither is
    # guaranteed to have a response at all on a transport-level failure.
    try {
        $response = Get-NCProperty $ErrorRecord.Exception 'Response'
        if ($null -ne $response) {
            $status = Get-NCProperty $response 'StatusCode'
            if ($null -ne $status) { return [int]$status }
        }
    } catch {
        # No usable response object — treat as a transport failure.
    }
    return 0
}

function Enable-NCTls12 {
    # Windows PowerShell 5.1 defaults to SSL3/TLS1.0, which most endpoints now
    # refuse. PowerShell 7 negotiates properly and needs no help.
    if ($PSVersionTable.PSEdition -eq 'Core') { return }

    try {
        $current = [System.Net.ServicePointManager]::SecurityProtocol
        if (-not ($current -band [System.Net.SecurityProtocolType]::Tls12)) {
            [System.Net.ServicePointManager]::SecurityProtocol = $current -bor [System.Net.SecurityProtocolType]::Tls12
        }
    } catch {
        Write-Verbose 'Could not raise the TLS version; leaving the process default in place.'
    }
}

# ---------------------------------------------------------------------------
# Connection
# ---------------------------------------------------------------------------

function Connect-NCTelemetry {
    <#
    .SYNOPSIS
        Configures the endpoint and starts a fresh buffer.

    .DESCRIPTION
        Call once before staging values. Calling again discards anything buffered
        and starts over.

        RetainMinutes must exceed the interval between sends or values expire
        between them. For a script that sends once and exits, retain is what gives
        you a dead man's switch: if the script fails to run at all, nothing arrives,
        the status expires, and NetCrunch raises the alert on its own.

    .PARAMETER Endpoint
        Full URL shown on the Telemetry sensor form. Treat it as a secret — it is
        not written to logs or error messages by this module.

    .PARAMETER Token
        Bearer token from the Telemetry sensor, sent as an Authorization header.
        Optional only because the receiver does not yet require one; see
        spec/v1.md section 1.1.

    .EXAMPLE
        Connect-NCTelemetry -Endpoint $env:NC_TELEMETRY_URL -RetainMinutes 90
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Endpoint,

        [ValidateNotNullOrEmpty()]
        [string]$Token,

        [ValidateRange(1, 1440)]
        [int]$RetainMinutes = 5,

        [ValidateRange(1, 525600)]
        [int]$RemoveMinutes = 1440,

        [ValidateRange(1, 600)]
        [int]$TimeoutSeconds = 30,

        [ValidateRange(0, 10)]
        [int]$MaxRetries = 3
    )

    $uri = $null
    if (-not [Uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$uri) -or
        ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
        throw 'Endpoint must be an absolute http or https URL.'
    }
    if ($uri.Scheme -eq 'http') {
        Write-Warning 'Endpoint uses http. The URL carries the sensor identity — prefer https.'
    }

    $script:State = @{
        Endpoint       = $Endpoint
        Token          = $Token
        RetainMinutes  = $RetainMinutes
        RemoveMinutes  = $RemoveMinutes
        TimeoutSeconds = $TimeoutSeconds
        MaxRetries     = $MaxRetries
        Counters       = [ordered]@{}
        Statuses       = [ordered]@{}
        Timestamps     = [ordered]@{}
        DataObjects    = [ordered]@{}
        Events         = New-Object System.Collections.ArrayList
    }
}

function Disconnect-NCTelemetry {
    <#
    .SYNOPSIS
        Discards the endpoint and any buffered values.
    #>
    [CmdletBinding()]
    param()

    $script:State = $null
}

function Clear-NCTelemetry {
    <#
    .SYNOPSIS
        Empties the buffer without disconnecting.
    #>
    [CmdletBinding()]
    param()

    Assert-NCConnected
    $script:State.Counters    = [ordered]@{}
    $script:State.Statuses    = [ordered]@{}
    $script:State.Timestamps  = [ordered]@{}
    $script:State.DataObjects = [ordered]@{}
    $script:State.Events      = New-Object System.Collections.ArrayList
}

# ---------------------------------------------------------------------------
# Staging values
#
# Object/Counter/Value are validated by hand rather than declared Mandatory.
# Mandatory parameters *prompt* when omitted in an interactive host, which would
# hang the conformance suite instead of failing it. Explicit checks also produce
# better messages than parameter binding does.
# ---------------------------------------------------------------------------

function Set-NCCounter {
    <#
    .SYNOPSIS
        Stages a numeric value. Setting the same object/counter/instance again
        replaces the previous value.

    .DESCRIPTION
        A counter that stops being sent expires on its own after the retain time
        and is removed after the remove time, so short-lived counters need no
        explicit cleanup.

        NetCrunch does not derive per-second rates for telemetry counters. Send a
        rate only if you have computed it yourself.

    .EXAMPLE
        Set-NCCounter -Object 'Backup' -Counter 'Files Copied' -Value $count

    .EXAMPLE
        Set-NCCounter -Object 'Queue' -Counter 'Depth' -Instance 'inbound' -Value 5
    #>
    [CmdletBinding()]
    param(
        [string]$Object,
        [string]$Counter,
        [object]$Value,
        [string]$Instance
    )

    Assert-NCConnected

    if ([string]::IsNullOrWhiteSpace($Object))  { throw 'Object is required and must be a non-empty string.' }
    if ([string]::IsNullOrWhiteSpace($Counter)) { throw 'Counter is required and must be a non-empty string.' }
    if (-not (Test-NCNumeric $Value)) {
        throw "Counter value must be a number. Got '$($Value)'. Use Set-NCStatus for text values."
    }

    $key = New-NCCounterKey -Object $Object -Counter $Counter -Instance $Instance
    $script:State.Counters[$key] = @{
        Object   = $Object
        Counter  = $Counter
        Instance = $Instance
        Value    = $Value
    }
}

function Set-NCStatus {
    <#
    .SYNOPSIS
        Stages a state with an optional explanation. Statuses are what NetCrunch
        alerting acts on — counters are not.

    .DESCRIPTION
        The receiver discards a status whose value is empty or not a string, and it
        does so without raising anything. This cmdlet rejects those locally instead,
        so the failure surfaces where you can see it.

    .EXAMPLE
        Set-NCStatus -Key 'Nightly Backup' -Value 'OK' -Message "$count files in $duration"

    .EXAMPLE
        Set-NCStatus -Key 'Nightly Backup' -Value 'Error' -Message $_.Exception.Message -Critical
    #>
    [CmdletBinding()]
    param(
        [string]$Key,
        [object]$Value,
        [string]$Message,
        [switch]$Critical,
        [object]$Data
    )

    Assert-NCConnected

    if ([string]::IsNullOrWhiteSpace($Key)) { throw 'Key is required and must be a non-empty string.' }
    if ($Key.StartsWith('@')) {
        throw "Status key '$Key' is reserved. Keys beginning with '@' are used internally by NetCrunch."
    }
    if ($Key.Length -gt $script:MaxStatusKeyLength) {
        throw "Status key is $($Key.Length) characters. NetCrunch truncates keys at $script:MaxStatusKeyLength."
    }
    if ($Value -isnot [string]) {
        throw "Status value must be a string. Got $($Value.GetType().Name). Use Set-NCCounter for numbers."
    }
    if ([string]::IsNullOrEmpty($Value)) {
        throw 'Status value must not be empty. NetCrunch discards empty statuses without reporting it.'
    }

    $status = [ordered]@{ value = $Value }
    if ($PSBoundParameters.ContainsKey('Message') -and -not [string]::IsNullOrEmpty($Message)) {
        $status['message'] = $Message
    }
    if ($Critical.IsPresent) { $status['critical'] = $true }
    if ($PSBoundParameters.ContainsKey('Data') -and $null -ne $Data) { $status['data'] = $Data }

    $script:State.Statuses[$Key] = $status
}

function Add-NCEvent {
    <#
    .SYNOPSIS
        Stages a discrete occurrence.

    .DESCRIPTION
        Events accumulate; each call adds one. Use a status for a condition that
        begins and later ends — an event is a thing that happened at a point in time.

        Events are cleared once sent successfully, so a script that sends more than
        once will not repeat them.

    .EXAMPLE
        Add-NCEvent -Message 'Nightly backup completed'
    #>
    [CmdletBinding()]
    param(
        [object]$Message,
        [ValidateSet('info', 'warning', 'error')]
        [string]$Severity
    )

    Assert-NCConnected

    if ($Message -isnot [string]) {
        throw "Event message must be a string. Got $(if ($null -eq $Message) { 'null' } else { $Message.GetType().Name })."
    }
    if ([string]::IsNullOrWhiteSpace($Message)) {
        throw 'Event message must not be empty. NetCrunch discards such events without reporting it.'
    }

    $event = [ordered]@{ message = $Message }
    if ($PSBoundParameters.ContainsKey('Severity')) { $event['severity'] = $Severity }

    [void]$script:State.Events.Add($event)
}

# ---------------------------------------------------------------------------
# Data objects
# ---------------------------------------------------------------------------

function Assert-NCDataArray {
    param([string]$Name, $Value)

    if ($Value -isnot [System.Collections.IList]) {
        throw "$Name is required and must be an array."
    }
    if ($Value.Count -gt $script:MaxDataEntries) {
        throw "$Name has $($Value.Count) entries. NetCrunch truncates at $script:MaxDataEntries without reporting it."
    }
}

function New-NCDataObject {
    param(
        [string]$Id,
        [string]$Type,
        [hashtable]$Members,
        [string]$Name,
        [string]$SeriesName,
        [string]$Message,
        [string]$Status
    )

    Assert-NCConnected

    if ([string]::IsNullOrWhiteSpace($Id)) {
        throw 'Id is required and must be a non-empty string. It is the object identity across payloads.'
    }

    $object = [ordered]@{ type = $Type }
    foreach ($key in $Members.Keys) { $object[$key] = $Members[$key] }

    if (-not [string]::IsNullOrEmpty($Name))       { $object['name'] = $Name }
    if (-not [string]::IsNullOrEmpty($SeriesName)) { $object['seriesName'] = $SeriesName }
    if (-not [string]::IsNullOrEmpty($Message))    { $object['message'] = $Message }
    if (-not [string]::IsNullOrEmpty($Status))     { $object['status'] = $Status }

    $script:State.DataObjects[$Id] = $object
}

function Set-NCTable {
    <#
    .SYNOPSIS
        Stages a table rendered on the sensor's page.

    .DESCRIPTION
        Staging the same Id again replaces the table — a data object is a whole
        view each time, with no incremental form.

        Rows is an array of arrays. PowerShell unrolls a single nested array, so
        one row needs the comma operator: -Rows @(, @('a', 1)).

    .EXAMPLE
        Set-NCTable -Id 'services' -Name 'Stopped Services' `
                    -Columns 'Name', 'StartType' `
                    -Rows @(, @('wuauserv', 'Manual'))
    #>
    [CmdletBinding()]
    param(
        [string]$Id,
        [object[]]$Columns,
        [object[]]$Rows,
        [string]$Name,
        [string]$Message,
        [string]$Status
    )

    Assert-NCConnected
    Assert-NCDataArray -Name 'Columns' -Value $Columns
    Assert-NCDataArray -Name 'Rows' -Value $Rows

    # A ragged table is the dangerous case: nothing errors anywhere and the page
    # simply renders the wrong thing.
    for ($i = 0; $i -lt $Rows.Count; $i++) {
        $row = $Rows[$i]
        if ($row -isnot [System.Collections.IList]) {
            throw "Row $i must be an array of cells. A single row needs the comma operator: -Rows @(, @('a', 1))."
        }
        if ($row.Count -ne $Columns.Count) {
            throw "Row $i has $($row.Count) cells but there are $($Columns.Count) columns."
        }
    }

    New-NCDataObject -Id $Id -Type 'table' -Members @{ columns = $Columns; rows = $Rows } `
                     -Name $Name -Message $Message -Status $Status
}

function Set-NCTimeSeries {
    <#
    .SYNOPSIS
        Stages a time series chart. Timestamps are epoch milliseconds.

    .EXAMPLE
        Set-NCTimeSeries -Id 'throughput' -Name 'Throughput' -SeriesName 'Rows/sec' `
                         -Timestamps $stamps -Values $rates
    #>
    [CmdletBinding()]
    param(
        [string]$Id,
        [object[]]$Timestamps,
        [object[]]$Values,
        [string]$Name,
        [string]$SeriesName,
        [string]$Message,
        [string]$Status
    )

    Assert-NCConnected
    Assert-NCDataArray -Name 'Timestamps' -Value $Timestamps
    Assert-NCDataArray -Name 'Values' -Value $Values

    if ($Timestamps.Count -ne $Values.Count) {
        throw "Timestamps has $($Timestamps.Count) entries but Values has $($Values.Count); they must match."
    }

    New-NCDataObject -Id $Id -Type 'time-series' -Members @{ timestamps = $Timestamps; values = $Values } `
                     -Name $Name -SeriesName $SeriesName -Message $Message -Status $Status
}

function Set-NCCategoryChart {
    <#
    .SYNOPSIS
        Stages a labelled bar chart.

    .EXAMPLE
        Set-NCCategoryChart -Id 'byOutcome' -Name 'Items by Outcome' -SeriesName 'Items' `
                            -Categories 'imported', 'skipped', 'failed' -Values 1204, 18, 3
    #>
    [CmdletBinding()]
    param(
        [string]$Id,
        [object[]]$Categories,
        [object[]]$Values,
        [string]$Name,
        [string]$SeriesName,
        [string]$Message,
        [string]$Status
    )

    Assert-NCConnected
    Assert-NCDataArray -Name 'Categories' -Value $Categories
    Assert-NCDataArray -Name 'Values' -Value $Values

    if ($Categories.Count -ne $Values.Count) {
        throw "Categories has $($Categories.Count) entries but Values has $($Values.Count); they must match."
    }

    New-NCDataObject -Id $Id -Type 'category' -Members @{ categories = $Categories; values = $Values } `
                     -Name $Name -SeriesName $SeriesName -Message $Message -Status $Status
}

function Set-NCTimestamp {
    <#
    .SYNOPSIS
        Records when something last happened, as an age counter plus a readable status.

    .DESCRIPTION
        The wire format has no timestamp type, and a raw clock value means nothing
        outside the process that produced it. So a recorded instant becomes two
        things: an age in seconds, which an alert threshold can be set on, and a
        status message carrying the absolute time, which a person can read.

        Age is computed when the payload is built, not when this is called.

    .EXAMPLE
        Set-NCTimestamp -Object 'Sync' -Counter 'Last Success Age s' -StatusKey 'Last Sync'
    #>
    [CmdletBinding()]
    param(
        [string]$Object,
        [string]$Counter,
        [string]$StatusKey,
        [datetime]$ObservedAt = (Get-Date),
        [string]$StatusValue = 'OK'
    )

    Assert-NCConnected

    if ([string]::IsNullOrWhiteSpace($Object))    { throw 'Object is required and must be a non-empty string.' }
    if ([string]::IsNullOrWhiteSpace($Counter))   { throw 'Counter is required and must be a non-empty string.' }
    if ([string]::IsNullOrWhiteSpace($StatusKey)) { throw 'StatusKey is required and must be a non-empty string.' }

    $script:State.Timestamps[$StatusKey] = @{
        Object      = $Object
        Counter     = $Counter
        StatusKey   = $StatusKey
        ObservedAt  = $ObservedAt
        StatusValue = $StatusValue
    }
}

# ---------------------------------------------------------------------------
# Payload
# ---------------------------------------------------------------------------

function Get-NCTelemetryPayload {
    <#
    .SYNOPSIS
        Builds the payload that Send-NCTelemetry would post, without sending it.

    .DESCRIPTION
        Useful for testing and for seeing exactly what a script reports. Members
        with nothing in them are omitted rather than sent empty.

    .PARAMETER SnapshotAt
        The instant to measure timestamp ages against. Defaults to now; exists so
        the result is reproducible under test.

    .EXAMPLE
        Get-NCTelemetryPayload -AsJson
    #>
    [CmdletBinding()]
    param(
        [datetime]$SnapshotAt = (Get-Date),
        [switch]$AsJson
    )

    Assert-NCConnected

    $payload = [ordered]@{
        retain = $script:State.RetainMinutes
        remove = $script:State.RemoveMinutes
    }

    $counters = New-Object System.Collections.ArrayList
    foreach ($entry in $script:State.Counters.Values) {
        $path = [ordered]@{
            object  = $entry.Object
            counter = $entry.Counter
        }
        if (-not [string]::IsNullOrEmpty($entry.Instance)) { $path['instance'] = $entry.Instance }
        [void]$counters.Add([ordered]@{ path = $path; value = $entry.Value })
    }

    # A timestamp contributes to both collections, so it is expanded here rather
    # than at the call site — the age is only meaningful relative to this snapshot.
    $statuses = [ordered]@{}
    foreach ($key in $script:State.Statuses.Keys) { $statuses[$key] = $script:State.Statuses[$key] }

    foreach ($stamp in $script:State.Timestamps.Values) {
        $ageSeconds = [int][math]::Round(($SnapshotAt.ToUniversalTime() - $stamp.ObservedAt.ToUniversalTime()).TotalSeconds)
        [void]$counters.Add([ordered]@{
            path  = [ordered]@{ object = $stamp.Object; counter = $stamp.Counter }
            value = $ageSeconds
        })
        $statuses[$stamp.StatusKey] = [ordered]@{
            value   = $stamp.StatusValue
            message = Format-NCTimestamp -Value $stamp.ObservedAt
        }
    }

    if ($counters.Count -gt 0)                  { $payload['counters'] = $counters.ToArray() }
    if ($statuses.Count -gt 0)                  { $payload['statuses'] = $statuses }
    if ($script:State.Events.Count -gt 0)       { $payload['events']   = $script:State.Events.ToArray() }
    if ($script:State.DataObjects.Count -gt 0)  { $payload['data']     = $script:State.DataObjects }

    if ($AsJson.IsPresent) {
        return ConvertTo-Json -InputObject $payload -Depth $script:JsonDepth -Compress
    }
    return $payload
}

# ---------------------------------------------------------------------------
# Sending
# ---------------------------------------------------------------------------

function Send-NCTelemetry {
    <#
    .SYNOPSIS
        Posts everything buffered as a single payload.

    .DESCRIPTION
        Sends one request carrying every staged value. The receiver caps pending
        payloads per sensor and drops the overflow silently, so batching is not
        just an optimisation.

        The payload carries absolute values, which makes the request idempotent —
        a retry after a timeout cannot double-count. Transport failures and 5xx
        responses are therefore retried; 4xx responses are not, since repeating a
        rejected request will not change the answer.

        Events are cleared on success. Counters and statuses are kept, so a script
        that loops keeps reporting current values without restating them.

    .EXAMPLE
        Send-NCTelemetry

    .EXAMPLE
        Send-NCTelemetry -WhatIf
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [datetime]$SnapshotAt = (Get-Date),
        [switch]$PassThru
    )

    Assert-NCConnected

    $payload = Get-NCTelemetryPayload -SnapshotAt $SnapshotAt
    if ($payload.Count -le 2) {
        Write-Verbose 'Nothing staged; skipping the send.'
        return
    }

    $json = ConvertTo-Json -InputObject $payload -Depth $script:JsonDepth -Compress
    $body = [System.Text.Encoding]::UTF8.GetBytes($json)

    if (-not $PSCmdlet.ShouldProcess('NetCrunch Telemetry sensor', "Send $($body.Length) bytes")) {
        return
    }

    Enable-NCTls12

    $headers = @{}
    if (-not [string]::IsNullOrEmpty($script:State.Token)) {
        $headers['Authorization'] = "Bearer $($script:State.Token)"
    }

    $attempt  = 0
    $lastError = $null

    while ($attempt -le $script:State.MaxRetries) {
        $attempt++
        try {
            $null = Invoke-RestMethod -Uri $script:State.Endpoint `
                                      -Method Post `
                                      -Body $body `
                                      -Headers $headers `
                                      -ContentType 'application/json; charset=utf-8' `
                                      -TimeoutSec $script:State.TimeoutSeconds `
                                      -ErrorAction Stop

            [void]$script:State.Events.Clear()
            Write-Verbose "Telemetry sent on attempt $attempt ($($body.Length) bytes)."
            if ($PassThru.IsPresent) { return $payload }
            return

        } catch {
            $lastError  = $_
            $statusCode = Get-NCStatusCode -ErrorRecord $_
            $message    = Get-NCSafeErrorMessage -ErrorRecord $_ -StatusCode $statusCode

            $retryable = ($statusCode -eq 0) -or ($statusCode -eq 429) -or ($statusCode -ge 500)
            if (-not $retryable -or $attempt -gt $script:State.MaxRetries) {
                throw $message
            }

            $backoffSeconds = [math]::Min(30, [math]::Pow(2, $attempt - 1))
            Write-Verbose "$message Retrying in $backoffSeconds s (attempt $attempt of $($script:State.MaxRetries + 1))."
            Start-Sleep -Seconds $backoffSeconds
        }
    }

    throw (Get-NCSafeErrorMessage -ErrorRecord $lastError -StatusCode (Get-NCStatusCode -ErrorRecord $lastError))
}

Export-ModuleMember -Function @(
    'Connect-NCTelemetry'
    'Disconnect-NCTelemetry'
    'Clear-NCTelemetry'
    'Set-NCCounter'
    'Set-NCStatus'
    'Add-NCEvent'
    'Set-NCTimestamp'
    'Set-NCTable'
    'Set-NCTimeSeries'
    'Set-NCCategoryChart'
    'Get-NCTelemetryPayload'
    'Send-NCTelemetry'
)
