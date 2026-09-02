#Requires -Version 7.0
<#
.SYNOPSIS
Reads and safely manages KO Lite jobs through the versioned localhost agent API.

.DESCRIPTION
Drives /api/v1 with named request contracts, Problem Details errors, ETag/If-Match
catalog concurrency, repair preview approval, and optional cursor continuation.
It never exposes hard delete, whole-slice rerun, Kusto cleanup, or arbitrary Kusto writes.

.PARAMETER Action
Health, System-Status, Get-Jobs, Get-Job, Create, Update, Export, Import, Pause,
Resume, Soft-Delete, Restore, Preview-Repair, Repair, Get-JobStatus,
Get-CatalogRevisions, Get-Dependencies, Get-WorkerPool, Get-Queue, Get-Slices,
Get-RunningSlices, Get-Chunks, Get-Attempts, Get-Events, Get-Logs,
Get-Throughput, Get-Failures, Get-Audit, Get-Reruns, Get-Rerun,
Get-Repairs, or Get-Repair.

.PARAMETER JobId
Permanent job GUID. Get-Job also accepts an exact activityId and resolves it
through the list filter before using the GUID-keyed detail route.

.PARAMETER Json
Schedule JSON for Create, Update, or Import. Import accepts one object or an array;
the helper normalizes either shape into the API's schedules envelope.

.PARAMETER Path
Schedule JSON file for Create, Update, or Import.

.PARAMETER Query
Query filters for operational reads. Use limit and cursor for paging. When
-AllPages is supplied, the helper follows nextCursor until the collection ends.

.PARAMETER BatchId
Rerun or repair batch ID for Get-Rerun or Get-Repair.

.PARAMETER Reason
Audit reason for lifecycle commands; required for Repair.

.PARAMETER From
Inclusive repair range start. Sent as an explicit UTC Z instant.

.PARAMETER To
Exclusive repair range end. Sent as an explicit UTC Z instant.

.PARAMETER ExpectedSliceCount
Repairable slice count returned by Preview-Repair.

.PARAMETER ExpectedExecutionCount
Repairable execution count returned by Preview-Repair for chunked work.

.PARAMETER PreviewToken
Opaque preview token returned by Preview-Repair for chunked work.

.PARAMETER Force
Soft-Delete only: override the active-dependent guard.

.PARAMETER AllPages
Follow opaque nextCursor values for paged operational collections.

.PARAMETER SkipValidation
Skip the local schedule validator before Create, Update, or Import.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet(
        'Health', 'System-Status', 'Get-Jobs', 'Get-Job', 'Create', 'Update',
        'Export', 'Import', 'Pause', 'Resume', 'Soft-Delete', 'Restore',
        'Preview-Repair', 'Repair',
        'Get-JobStatus', 'Get-CatalogRevisions', 'Get-Dependencies',
        'Get-WorkerPool', 'Get-Queue', 'Get-Slices', 'Get-RunningSlices',
        'Get-Chunks', 'Get-Attempts', 'Get-Events', 'Get-Logs',
        'Get-Throughput', 'Get-Failures', 'Get-Audit',
        'Get-Reruns', 'Get-Rerun', 'Get-Repairs', 'Get-Repair')]
    [string]$Action,

    [string]$BaseUrl = 'http://127.0.0.1:5057',
    [string]$JobId,
    [string]$BatchId,
    [hashtable]$Query = @{},
    [string]$Json,
    [string]$Path,
    [string]$Reason,
    [Nullable[datetime]]$From,
    [Nullable[datetime]]$To,
    [int]$ExpectedSliceCount,
    [int]$ExpectedExecutionCount,
    [string]$PreviewToken,
    [switch]$Force,
    [switch]$AllPages,
    [switch]$SkipValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$apiRoot = '/api/v1'
$expectedSliceCountSupplied = $PSBoundParameters.ContainsKey('ExpectedSliceCount')
$expectedExecutionCountSupplied = $PSBoundParameters.ContainsKey('ExpectedExecutionCount')

function ConvertFrom-ResponseJson {
    param([AllowEmptyString()][string]$Content)

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return $null
    }

    try {
        return $Content | ConvertFrom-Json -Depth 100
    } catch {
        return $Content
    }
}

function Invoke-KoLiteApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$RelativeUri,
        [string]$Body,
        [hashtable]$Headers = @{}
    )

    $uri = "$($BaseUrl.TrimEnd('/'))$RelativeUri"
    $arguments = @{
        Method             = $Method
        Uri                = $uri
        SkipHttpErrorCheck = $true
        TimeoutSec         = 30
        Headers            = $Headers
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $arguments['Body'] = $Body
        $arguments['ContentType'] = 'application/json'
    }

    try {
        $response = Invoke-WebRequest @arguments
    } catch [System.Net.Http.HttpRequestException] {
        throw "Could not reach KO Lite at $uri. Is the app running? ($($_.Exception.Message))"
    }

    $content = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $parsed = ConvertFrom-ResponseJson -Content $content
    if ([int]$response.StatusCode -ge 400) {
        $code = if ($null -ne $parsed -and $parsed.PSObject.Properties.Name -contains 'code') { [string]$parsed.code } else { 'unknown-error' }
        $detail = if ($null -ne $parsed -and $parsed.PSObject.Properties.Name -contains 'detail') { [string]$parsed.detail } else { $content }
        throw "KO Lite API call failed: $Method $RelativeUri returned HTTP $([int]$response.StatusCode) ($code). $detail"
    }

    return [pscustomobject]@{
        Body       = $parsed
        StatusCode = [int]$response.StatusCode
        Headers    = $response.Headers
        Location   = [string]$response.Headers['Location']
        ETag       = [string]$response.Headers['ETag']
    }
}

function Assert-SupportedApi {
    $status = (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/system/status").Body
    if (@($status.supportedApiVersions) -notcontains 'v1') {
        throw "The running KO Lite app does not advertise agent API v1. Update the app or use a matching helper before attempting writes."
    }
}

function Get-ScheduleText {
    if (-not [string]::IsNullOrWhiteSpace($Json)) {
        if (-not [string]::IsNullOrWhiteSpace($Path)) {
            throw 'Specify only one of -Json or -Path.'
        }
        return $Json
    }

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            throw "Schedule JSON file not found: $Path"
        }
        return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).Path)
    }

    throw "$Action requires -Json or -Path."
}

function Test-ScheduleText {
    param([Parameter(Mandatory)][string]$ScheduleText)

    if ($SkipValidation) {
        return
    }

    $validator = Join-Path $PSScriptRoot '..\..\ko-lite-schedule-json\scripts\Test-KoLiteScheduleJson.ps1'
    if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
        throw "Schedule validator not found at $validator. Use -SkipValidation only after validating the JSON another way."
    }

    $results = & $validator -Json $ScheduleText -Quiet -FailOnError:$false
    $invalid = @($results | Where-Object { -not $_.IsValid })
    if ($invalid.Count -gt 0) {
        $messages = $invalid | ForEach-Object {
            $label = if ($_.ActivityId) { $_.ActivityId } else { "index $($_.Index)" }
            "  - ${label}: $($_.Errors -join '; ')"
        }
        throw "Schedule JSON failed local validation; nothing was sent:`n$($messages -join "`n")"
    }
}

function Resolve-JobGuid {
    param([Parameter(Mandatory)][string]$Reference)

    $guid = [guid]::Empty
    if ([guid]::TryParse($Reference, [ref]$guid)) {
        return $guid.ToString('D')
    }

    $query = [uri]::EscapeDataString($Reference)
    $response = (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/jobs?activityId=$query").Body
    $matches = @($response.items)
    if ($matches.Count -ne 1) {
        throw "Could not resolve '$Reference' as a permanent GUID or one exact activityId."
    }

    return ([guid]$matches[0].jobId).ToString('D')
}

function Get-JobResponse {
    param([Parameter(Mandatory)][string]$Reference)

    $resolved = Resolve-JobGuid -Reference $Reference
    return Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/jobs/$resolved"
}

function ConvertTo-QueryString {
    param([hashtable]$Parameters)

    if ($null -eq $Parameters -or $Parameters.Count -eq 0) {
        return ''
    }

    $pairs = foreach ($key in $Parameters.Keys | Sort-Object) {
        $value = $Parameters[$key]
        if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
            continue
        }
        "$([uri]::EscapeDataString([string]$key))=$([uri]::EscapeDataString([string]$value))"
    }
    $pairs = @($pairs)
    if ($pairs.Count -eq 0) {
        return ''
    }

    return '?' + ($pairs -join '&')
}

function Invoke-PagedGet {
    param(
        [Parameter(Mandatory)][string]$RelativeUri,
        [hashtable]$Parameters
    )

    $allItems = [System.Collections.Generic.List[object]]::new()
    $cursor = $null
    do {
        $pageParameters = @{}
        foreach ($entry in $Parameters.GetEnumerator()) {
            $pageParameters[$entry.Key] = $entry.Value
        }
        if (-not [string]::IsNullOrWhiteSpace($cursor)) {
            $pageParameters['cursor'] = $cursor
        }

        $response = (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$RelativeUri$(ConvertTo-QueryString $pageParameters)").Body
        if ($null -eq $response -or -not ($response.PSObject.Properties.Name -contains 'items')) {
            return $response
        }

        foreach ($item in @($response.items)) {
            $allItems.Add($item)
        }
        $cursor = [string]$response.nextCursor
    } while ($AllPages -and -not [string]::IsNullOrWhiteSpace($cursor))

    if (-not $AllPages) {
        return $response
    }

    return [pscustomobject]@{
        items      = @($allItems)
        nextCursor = $null
    }
}

function Get-RepairBody {
    param([switch]$IncludeApproval)

    if ([string]::IsNullOrWhiteSpace($JobId)) {
        throw "$Action requires -JobId."
    }
    if ($null -eq $From -or $null -eq $To) {
        throw "$Action requires -From and -To."
    }

    $payload = [ordered]@{
        from = $From.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
        to   = $To.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
    }
    if (-not [string]::IsNullOrWhiteSpace($Reason)) {
        $payload['reason'] = $Reason.Trim()
    }
    if ($IncludeApproval) {
        if ([string]::IsNullOrWhiteSpace($Reason)) {
            throw 'Repair requires -Reason.'
        }
        if (-not $expectedSliceCountSupplied) {
            throw 'Repair requires -ExpectedSliceCount from Preview-Repair.'
        }
        if ([string]::IsNullOrWhiteSpace($PreviewToken)) {
            throw 'Repair requires -PreviewToken from Preview-Repair.'
        }
        $payload['expectedSliceCount'] = $ExpectedSliceCount
        if ($expectedExecutionCountSupplied) {
            $payload['expectedExecutionCount'] = $ExpectedExecutionCount
        }
        $payload['previewToken'] = $PreviewToken
    }

    return $payload | ConvertTo-Json -Compress
}

$operationalRoutes = @{
    'Get-WorkerPool'    = 'worker-pool'
    'Get-Queue'         = 'queue'
    'Get-Slices'        = 'slices'
    'Get-RunningSlices' = 'running-slices'
    'Get-Chunks'        = 'chunks'
    'Get-Attempts'      = 'attempts'
    'Get-Events'        = 'events'
    'Get-Logs'          = 'logs'
    'Get-Throughput'    = 'throughput'
    'Get-Failures'      = 'failures'
    'Get-Audit'         = 'audit-events'
    'Get-Reruns'        = 'reruns'
    'Get-Repairs'       = 'repairs'
}

switch ($Action) {
    'Health' {
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri '/healthz').Body
    }
    'System-Status' {
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/system/status").Body
    }
    'Get-Jobs' {
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/jobs$(ConvertTo-QueryString $Query)").Body.items
    }
    'Get-Job' {
        if ([string]::IsNullOrWhiteSpace($JobId)) { throw 'Get-Job requires -JobId.' }
        return (Get-JobResponse -Reference $JobId).Body
    }
    'Create' {
        Assert-SupportedApi
        $schedule = Get-ScheduleText
        Test-ScheduleText -ScheduleText $schedule
        $payload = @{ schedule = ($schedule | ConvertFrom-Json -Depth 100) } | ConvertTo-Json -Depth 100 -Compress
        $result = Invoke-KoLiteApi -Method 'POST' -RelativeUri "$apiRoot/jobs" -Body $payload
        Write-Host "Created job $($result.Body.job.jobId); ETag $($result.ETag)."
        return $result.Body
    }
    'Update' {
        Assert-SupportedApi
        if ([string]::IsNullOrWhiteSpace($JobId)) { throw 'Update requires -JobId.' }
        $schedule = Get-ScheduleText
        Test-ScheduleText -ScheduleText $schedule
        $current = Get-JobResponse -Reference $JobId
        $resolved = ([guid]$current.Body.job.jobId).ToString('D')
        $payload = @{ schedule = ($schedule | ConvertFrom-Json -Depth 100) } | ConvertTo-Json -Depth 100 -Compress
        return (Invoke-KoLiteApi -Method 'PUT' -RelativeUri "$apiRoot/jobs/$resolved" -Body $payload -Headers @{ 'If-Match' = $current.ETag }).Body
    }
    'Export' {
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/jobs/export").Body
    }
    'Import' {
        Assert-SupportedApi
        $scheduleText = Get-ScheduleText
        Test-ScheduleText -ScheduleText $scheduleText
        $parsed = $scheduleText | ConvertFrom-Json -Depth 100
        $schedules = if ($parsed -is [System.Array]) { @($parsed) } else { @($parsed) }
        $payload = @{ schedules = $schedules } | ConvertTo-Json -Depth 100 -Compress
        return (Invoke-KoLiteApi -Method 'POST' -RelativeUri "$apiRoot/jobs/import" -Body $payload).Body
    }
    { $_ -in @('Pause', 'Resume', 'Soft-Delete', 'Restore') } {
        Assert-SupportedApi
        if ([string]::IsNullOrWhiteSpace($JobId)) { throw "$Action requires -JobId." }
        $current = Get-JobResponse -Reference $JobId
        $resolved = ([guid]$current.Body.job.jobId).ToString('D')
        $actionName = $Action.ToLowerInvariant()
        $payload = [ordered]@{}
        if (-not [string]::IsNullOrWhiteSpace($Reason)) { $payload['reason'] = $Reason.Trim() }
        if ($Action -eq 'Soft-Delete' -and $Force) { $payload['force'] = $true }
        return (Invoke-KoLiteApi `
            -Method 'POST' `
            -RelativeUri "$apiRoot/jobs/$resolved/actions/$actionName" `
            -Body ($payload | ConvertTo-Json -Compress) `
            -Headers @{ 'If-Match' = $current.ETag }).Body
    }
    'Preview-Repair' {
        $resolved = Resolve-JobGuid -Reference $JobId
        return (Invoke-KoLiteApi -Method 'POST' -RelativeUri "$apiRoot/jobs/$resolved/repair-previews" -Body (Get-RepairBody)).Body
    }
    'Repair' {
        Assert-SupportedApi
        $resolved = Resolve-JobGuid -Reference $JobId
        $result = Invoke-KoLiteApi -Method 'POST' -RelativeUri "$apiRoot/jobs/$resolved/repairs" -Body (Get-RepairBody -IncludeApproval)
        Write-Host "Repair accepted at $($result.Location)."
        return $result.Body
    }
    'Get-JobStatus' {
        $resolved = Resolve-JobGuid -Reference $JobId
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/jobs/$resolved/status").Body
    }
    'Get-CatalogRevisions' {
        $resolved = Resolve-JobGuid -Reference $JobId
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/jobs/$resolved/catalog-revisions").Body
    }
    'Get-Dependencies' {
        $resolved = Resolve-JobGuid -Reference $JobId
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/jobs/$resolved/dependencies").Body
    }
    'Get-Rerun' {
        if ([string]::IsNullOrWhiteSpace($BatchId)) { throw 'Get-Rerun requires -BatchId.' }
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/operations/reruns/$([uri]::EscapeDataString($BatchId))").Body
    }
    'Get-Repair' {
        if ([string]::IsNullOrWhiteSpace($BatchId)) { throw 'Get-Repair requires -BatchId.' }
        return (Invoke-KoLiteApi -Method 'GET' -RelativeUri "$apiRoot/operations/repairs/$([uri]::EscapeDataString($BatchId))").Body
    }
    default {
        if (-not $operationalRoutes.ContainsKey($Action)) {
            throw "Unhandled action '$Action'."
        }

        $parameters = @{}
        foreach ($entry in $Query.GetEnumerator()) {
            $parameters[$entry.Key] = $entry.Value
        }
        if (-not [string]::IsNullOrWhiteSpace($JobId)) {
            $parameters['jobId'] = Resolve-JobGuid -Reference $JobId
        }
        return Invoke-PagedGet -RelativeUri "$apiRoot/operations/$($operationalRoutes[$Action])" -Parameters $parameters
    }
}
