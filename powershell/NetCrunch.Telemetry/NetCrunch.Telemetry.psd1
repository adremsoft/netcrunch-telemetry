@{
    RootModule        = 'NetCrunch.Telemetry.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '8f3a1c2e-5b47-4d19-9e6a-7c0d2f4b8a31'

    Author            = 'AdRem Software'
    CompanyName       = 'AdRem Software sp. z o.o.'
    Copyright         = 'Copyright 2026 AdRem Software sp. z o.o. Licensed under the Apache License, Version 2.0.'

    Description       = 'Push metrics, states and events from PowerShell scripts and scheduled tasks into NetCrunch.'

    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')

    FunctionsToExport = @(
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
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('NetCrunch', 'Monitoring', 'Telemetry', 'Metrics', 'Observability')
            LicenseUri   = 'https://github.com/adremsoft/netcrunch-telemetry/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/adremsoft/netcrunch-telemetry'
            ReleaseNotes = 'Initial implementation. Implements the v1 subset of the NetCrunch telemetry wire format: counters, statuses and events, sent as a single batched payload.'
            Prerelease   = 'alpha'
        }
    }
}
