#Requires -Version 7.0
<#
.SYNOPSIS
    Reads jobs and creates/updates schedules in a running KO Lite app through its
    localhost JSON API.

.DESCRIPTION
    Thin, rigorous wrapper around the KO Lite local management API
    (see docs\local-api.md). Schedule writes go through POST /api/jobs/import, the
    same validated, additive/update-only catalog path the dashboard import uses. It
    can also soft-delete and restore a job (POST /api/jobs/{id}/soft-delete and
    /restore), each requiring the job's current -ExpectedVersion, and re-run slices
    that already failed (POST /api/jobs/{id}/repair, preceded by a dry-run preview).
    This script intentionally exposes no hard-delete, enable/disable, direct Kusto,
    or rerun surface. Repair only re-runs Failed/DeadLettered slices; it can never
    touch a Completed slice, mark a slice complete without executing it, or delete
    anything from Kusto.

    Before importing, the body is validated locally with the sibling
    ko-lite-schedule-json validator unless -SkipValidation is supplied.

.PARAMETER Action
    Schedule read/write:
    Health     GET  /status/health  - confirm the app is up and print databasePath.
    Get-Jobs   GET  /api/jobs        - list job summaries.
    Get-Job    GET  /api/jobs/{id}   - one job summary plus its canonical schedule.
    Export     GET  /api/jobs/export - import-compatible array of all active jobs.
    Import     POST /api/jobs/import - create/update schedules (single object or array).
    Soft-Delete POST /api/jobs/{id}/soft-delete - hide a job (reversible); needs -ExpectedVersion.
    Restore     POST /api/jobs/{id}/restore     - un-hide a soft-deleted job; needs -ExpectedVersion.

    Repair (re-run slices that already failed):
    Preview-Repair POST /api/jobs/{id}/repair/preview - dry run; lists the Failed/DeadLettered
                   slices in -From..-To that would re-run. Writes nothing.
    Repair         POST /api/jobs/{id}/repair         - enqueue that set; needs -Reason and
                   -ExpectedSliceCount (the preview's repairableSliceCount).

    Read-only diagnostics (no mutation; pass filters via -Query):
    Per-job (require -JobId): Get-JobStatus, Get-Slices, Get-Chunks, Get-Attempts, Get-Events,
    Get-JobLogs, Get-JobQueue, Get-History, Get-JobThroughput, Get-Dependencies.
    Global: Get-WorkerPool, Get-RunningSlices, Get-Throughput, Get-Queue, Get-Logs,
    Get-Failures, Get-Audit, Get-Reruns, Get-Repairs.

.PARAMETER BaseUrl
    KO Lite base URL. Defaults to the loopback default http://127.0.0.1:5057.

.PARAMETER JobId
    The permanent GUID is preferred and is required for lifecycle writes.
    Get-Job also accepts an exact activityId and resolves it through Get-Jobs
    before calling the GUID-keyed route. Per-job diagnostics accept either the
    permanent GUID or activityId directly.

.PARAMETER Query
    Hashtable of query-string filters for diagnostics actions, e.g.
    @{ state = 'Running' }, @{ bucket = '30m'; groupBy = 'job' },
    @{ level = 'Warning'; take = 200 }, @{ batchId = '...' }. Empty values are
    dropped. Supported keys depend on the route (see docs\local-api.md): take,
    from, to, bucket, groupBy, state, level, category, action, subjectType,
    subjectId, jobId, batchId, start, end.

.PARAMETER Json
    Schedule JSON string (single object or array) for Import.

.PARAMETER Path
    Path to a schedule JSON file for Import (alternative to -Json).

.PARAMETER ExpectedVersion
    The job's current catalogVersion. Required by Soft-Delete and Restore for
    optimistic concurrency; read it first with Get-Job (the 'catalogVersion' field).
    A mismatch returns HTTP 409.

.PARAMETER Reason
    Optional audit reason recorded with a Soft-Delete or Restore. REQUIRED for
    Repair, where it is recorded on the repair batch and in the system audit trail.

.PARAMETER From
    Repair only: inclusive UTC start of the slice range, e.g. '2026-01-01T00:00:00Z'.
    Must land on the job's slice boundary; an unaligned value returns HTTP 400 naming
    the nearest aligned range.

.PARAMETER To
    Repair only: exclusive UTC end of the slice range. Same alignment rule as -From.

.PARAMETER ExpectedSliceCount
    Repair only (required): the 'repairableSliceCount' returned by Preview-Repair.
    If the count no longer matches, the call returns HTTP 409 instead of repairing a
    different set than the one that was approved. Always preview first.

.PARAMETER ExpectedExecutionCount
    Chunked repair only: the preview's repairableExecutionCount.

.PARAMETER PreviewToken
    Chunked repair only: the preview's exact opaque previewToken. A changed child
    identity/state/version causes HTTP 409 even if the parent slice count is unchanged.

.PARAMETER Force
    Soft-Delete only: proceed even when active downstream jobs depend on the target.
    Without it, Soft-Delete returns HTTP 409 listing the blocking dependents.

.PARAMETER SkipValidation
    Skip the local schedule-JSON validation step before Import. Not recommended.

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Health

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Get-Jobs

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Import -Path .\my-job.json

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Get-RunningSlices

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Get-Throughput -Query @{ bucket = '30m'; groupBy = 'job' }

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Get-Slices -JobId $id -Query @{ state = 'Running' }

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Soft-Delete -JobId $id -ExpectedVersion 3

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Restore -JobId $id -ExpectedVersion 4

.EXAMPLE
    # Re-run failed slices: always preview, then repair with the previewed count.
    $p = .\Invoke-KoLiteJobApi.ps1 -Action Preview-Repair -JobId $id -From '2026-01-01T00:00:00Z' -To '2026-01-02T00:00:00Z'
    .\Invoke-KoLiteJobApi.ps1 -Action Repair -JobId $id -From '2026-01-01T00:00:00Z' -To '2026-01-02T00:00:00Z' `
        -Reason 'Requeue slices that failed on a transient Kusto error' `
        -ExpectedSliceCount $p.repairableSliceCount `
        -ExpectedExecutionCount $p.repairableExecutionCount -PreviewToken $p.previewToken
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet(
        'Health', 'Get-Jobs', 'Get-Job', 'Export', 'Import', 'Soft-Delete', 'Restore',
        'Preview-Repair', 'Repair',
        'Get-JobStatus', 'Get-Slices', 'Get-Chunks', 'Get-Attempts', 'Get-Events', 'Get-JobLogs',
        'Get-JobQueue', 'Get-History', 'Get-JobThroughput', 'Get-Dependencies',
        'Get-WorkerPool', 'Get-RunningSlices', 'Get-Throughput', 'Get-Queue',
        'Get-Logs', 'Get-Failures', 'Get-Audit', 'Get-Reruns', 'Get-Repairs')]
    [string] $Action,

    [string] $BaseUrl = 'http://127.0.0.1:5057',

    [string] $JobId,

    [hashtable] $Query = @{},

    [string] $Json,

    [string] $Path,

    [long] $ExpectedVersion,

    [string] $Reason,

    [Nullable[datetime]] $From,

    [Nullable[datetime]] $To,

    [int] $ExpectedSliceCount,

    [int] $ExpectedExecutionCount,

    [string] $PreviewToken,

    [switch] $Force,

    [switch] $SkipValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-KoLiteApi {
    param(
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $RelativeUri,
        [string] $Body
    )

    $uri = "$($BaseUrl.TrimEnd('/'))$RelativeUri"
    $arguments = @{
        Method              = $Method
        Uri                 = $uri
        SkipHttpErrorCheck  = $true
        StatusCodeVariable  = 'status'
    }
    if ($PSBoundParameters.ContainsKey('Body') -and $null -ne $Body) {
        $arguments['Body'] = $Body
        $arguments['ContentType'] = 'application/json'
    }

    try {
        $response = Invoke-RestMethod @arguments
    }
    catch [System.Net.Http.HttpRequestException] {
        throw "Could not reach KO Lite at $uri. Is the app running? Start it with 'dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj'. ($($_.Exception.Message))"
    }

    if ($status -ge 400) {
        $detail = if ($null -ne $response -and ($response.PSObject.Properties.Name -contains 'error')) { $response.error } else { $response }
        throw "KO Lite API call failed: $Method $RelativeUri returned HTTP $status. $detail"
    }

    return $response
}

# Keep catalog reads GUID-keyed; resolve the mutable activityId only when the
# caller does not already have the permanent identity.
function Resolve-GetJobId {
    param([Parameter(Mandatory)] [string] $Reference)

    $guid = [guid]::Empty
    if ([guid]::TryParse($Reference, [ref]$guid)) {
        return $guid.ToString('N')
    }

    $response = Invoke-KoLiteApi -Method 'GET' -RelativeUri '/api/jobs'
    $matches = @($response.jobs | Where-Object {
        [string]::Equals(
            [string]$_.displayName,
            $Reference,
            [System.StringComparison]::Ordinal)
    })

    if ($matches.Count -eq 0) {
        throw "Get-Job could not resolve '$Reference' as a permanent GUID or exact activityId. Use -Action Get-Jobs to inspect available jobs."
    }
    if ($matches.Count -gt 1) {
        throw "Get-Job found multiple jobs with activityId '$Reference'. Use the permanent GUID instead."
    }

    return [string]$matches[0].jobId
}

function Get-ImportBody {
    if (-not [string]::IsNullOrWhiteSpace($Json)) {
        if (-not [string]::IsNullOrWhiteSpace($Path)) {
            throw 'Specify only one of -Json or -Path for Import.'
        }
        return $Json
    }

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            throw "Schedule JSON file not found: $Path"
        }
        return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).Path)
    }

    throw 'Import requires -Json or -Path.'
}

function Test-ImportBody {
    param([Parameter(Mandatory)] [string] $Body)

    $validator = Join-Path $PSScriptRoot '..\..\ko-lite-schedule-json\scripts\Test-KoLiteScheduleJson.ps1'
    if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
        throw "Schedule validator not found at $validator. Re-run with -SkipValidation only if you have validated the JSON another way."
    }

    $results = & $validator -Json $Body -Quiet -FailOnError:$false
    $invalid = @($results | Where-Object { -not $_.IsValid })
    if ($invalid.Count -gt 0) {
        $lines = $invalid | ForEach-Object {
            $id = if ($_.ActivityId) { $_.ActivityId } else { "index $($_.Index)" }
            "  - ${id}: $($_.Errors -join '; ')"
        }
        throw "Schedule JSON failed local validation; nothing was sent to KO Lite:`n$($lines -join "`n")"
    }
}

# Builds a '?k=v&...' query string from the -Query hashtable, URL-encoding keys and
# values and dropping empty ones. Diagnostics routes ignore unknown keys.
function Get-DiagnosticsQuery {
    param([hashtable] $Parameters)

    if ($null -eq $Parameters -or $Parameters.Count -eq 0) {
        return ''
    }

    $pairs = foreach ($key in $Parameters.Keys) {
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

# Read-only diagnostics routes. These never mutate state; they surface the same
# operational read models as the dashboard (see docs\local-api.md).
$globalDiagnostics = [ordered]@{
    'Get-WorkerPool'    = '/api/diagnostics/worker-pool'
    'Get-RunningSlices' = '/api/diagnostics/running-slices'
    'Get-Throughput'    = '/api/diagnostics/throughput'
    'Get-Queue'         = '/api/diagnostics/queue'
    'Get-Logs'          = '/api/diagnostics/logs'
    'Get-Failures'      = '/api/diagnostics/failures'
    'Get-Audit'         = '/api/diagnostics/audit'
    'Get-Reruns'        = '/api/diagnostics/reruns'
    'Get-Repairs'       = '/api/diagnostics/repairs'
}
$perJobDiagnostics = [ordered]@{
    'Get-JobStatus'     = 'status'
    'Get-Slices'        = 'slices'
    'Get-Chunks'        = 'chunks'
    'Get-Attempts'      = 'attempts'
    'Get-Events'        = 'events'
    'Get-JobLogs'       = 'logs'
    'Get-JobQueue'      = 'queue'
    'Get-History'       = 'history'
    'Get-JobThroughput' = 'throughput'
    'Get-Dependencies'  = 'dependencies'
}

# Builds the shared repair request body. -From/-To are sent as explicit UTC instants so the
# range the API validates against the job's slice grid is exactly the one the caller meant,
# regardless of the local machine's time zone. Pass them with a trailing 'Z'.
function Get-RepairBounds {
    param(
        [string] $Reason,
        [int] $ExpectedSliceCount,
        [int] $ExpectedExecutionCount,
        [string] $PreviewToken
    )

    if ([string]::IsNullOrWhiteSpace($JobId)) {
        throw "$Action requires -JobId (the permanent GUID or activityId)."
    }

    if ($null -eq $From -or $null -eq $To) {
        throw "$Action requires -From and -To (UTC slice bounds, e.g. '2026-01-01T00:00:00Z')."
    }

    $payload = [ordered]@{
        from = $From.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
        to   = $To.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
    }
    if (-not [string]::IsNullOrWhiteSpace($Reason)) { $payload['reason'] = $Reason }
    if ($PSBoundParameters.ContainsKey('ExpectedSliceCount')) { $payload['expectedSliceCount'] = $ExpectedSliceCount }
    if ($PSBoundParameters.ContainsKey('ExpectedExecutionCount')) { $payload['expectedExecutionCount'] = $ExpectedExecutionCount }
    if (-not [string]::IsNullOrWhiteSpace($PreviewToken)) { $payload['previewToken'] = $PreviewToken }

    return [pscustomobject]@{
        EncodedJobId = [uri]::EscapeDataString($JobId)
        Body         = ($payload | ConvertTo-Json -Compress)
    }
}

switch ($Action) {
    'Health' {
        return Invoke-KoLiteApi -Method 'GET' -RelativeUri '/status/health'
    }
    'Get-Jobs' {
        $response = Invoke-KoLiteApi -Method 'GET' -RelativeUri '/api/jobs'
        return $response.jobs
    }
    'Get-Job' {
        if ([string]::IsNullOrWhiteSpace($JobId)) {
            throw 'Get-Job requires -JobId (the permanent GUID or exact activityId).'
        }
        $resolvedJobId = Resolve-GetJobId -Reference $JobId
        $encoded = [uri]::EscapeDataString($resolvedJobId)
        return Invoke-KoLiteApi -Method 'GET' -RelativeUri "/api/jobs/$encoded"
    }
    'Export' {
        return Invoke-KoLiteApi -Method 'GET' -RelativeUri '/api/jobs/export'
    }
    'Import' {
        $body = Get-ImportBody
        if (-not $SkipValidation) {
            Test-ImportBody -Body $body
        }

        $result = Invoke-KoLiteApi -Method 'POST' -RelativeUri '/api/jobs/import' -Body $body
        Write-Host "Imported: $($result.created) created, $($result.updated) updated, $($result.total) total. No jobs were deleted."
        return $result
    }
    'Soft-Delete' {
        if ([string]::IsNullOrWhiteSpace($JobId)) {
            throw 'Soft-Delete requires -JobId (the permanent GUID).'
        }
        if (-not $PSBoundParameters.ContainsKey('ExpectedVersion')) {
            throw "Soft-Delete requires -ExpectedVersion (the job's current catalogVersion; read it with Get-Job)."
        }

        $payload = @{ expectedVersion = $ExpectedVersion }
        if (-not [string]::IsNullOrWhiteSpace($Reason)) { $payload['reason'] = $Reason }
        if ($Force) { $payload['force'] = $true }

        $encoded = [uri]::EscapeDataString($JobId)
        $result = Invoke-KoLiteApi -Method 'POST' -RelativeUri "/api/jobs/$encoded/soft-delete" -Body ($payload | ConvertTo-Json -Compress)
        Write-Host "Soft-deleted job '$JobId' (catalogVersion now $($result.job.catalogVersion)). Reversible with -Action Restore."
        return $result
    }
    'Restore' {
        if ([string]::IsNullOrWhiteSpace($JobId)) {
            throw 'Restore requires -JobId (the permanent GUID).'
        }
        if (-not $PSBoundParameters.ContainsKey('ExpectedVersion')) {
            throw "Restore requires -ExpectedVersion (the job's current catalogVersion; read it with Get-Job)."
        }

        $payload = @{ expectedVersion = $ExpectedVersion }
        if (-not [string]::IsNullOrWhiteSpace($Reason)) { $payload['reason'] = $Reason }

        $encoded = [uri]::EscapeDataString($JobId)
        $result = Invoke-KoLiteApi -Method 'POST' -RelativeUri "/api/jobs/$encoded/restore" -Body ($payload | ConvertTo-Json -Compress)
        Write-Host "Restored job '$JobId' (catalogVersion now $($result.job.catalogVersion))."
        return $result
    }
    'Preview-Repair' {
        $bounds = Get-RepairBounds
        $result = Invoke-KoLiteApi -Method 'POST' -RelativeUri "/api/jobs/$($bounds.EncodedJobId)/repair/preview" -Body $bounds.Body
        Write-Host "Repair preview: $($result.repairableSliceCount) logical slice(s) / $($result.repairableExecutionCount) execution(s) would re-run, $($result.blockedSliceCount) blocked, $($result.skippedSliceCount) untouched. Nothing was changed."
        $chunkSets = @($result.slices | Where-Object { $null -ne $_.chunkIds -and @($_.chunkIds).Count -gt 0 })
        foreach ($slice in $chunkSets) {
            Write-Host "  $($slice.startUtc) - $($slice.endUtc): chunk ID(s) $(@($slice.chunkIds) -join ', ')"
        }
        return $result
    }
    'Repair' {
        if ([string]::IsNullOrWhiteSpace($Reason)) {
            throw 'Repair requires -Reason; it is recorded on the repair batch and in the audit trail.'
        }
        if (-not $PSBoundParameters.ContainsKey('ExpectedSliceCount')) {
            throw "Repair requires -ExpectedSliceCount (the 'repairableSliceCount' from -Action Preview-Repair). Always preview first."
        }

        $repairArgs = @{
            Reason = $Reason
            ExpectedSliceCount = $ExpectedSliceCount
        }
        if ($PSBoundParameters.ContainsKey('ExpectedExecutionCount')) {
            $repairArgs['ExpectedExecutionCount'] = $ExpectedExecutionCount
        }
        if (-not [string]::IsNullOrWhiteSpace($PreviewToken)) {
            $repairArgs['PreviewToken'] = $PreviewToken
        }
        $bounds = Get-RepairBounds @repairArgs
        $result = Invoke-KoLiteApi -Method 'POST' -RelativeUri "/api/jobs/$($bounds.EncodedJobId)/repair" -Body $bounds.Body
        Write-Host "Repair batch $($result.repairBatchId): $($result.queued) slice(s) queued, $($result.blocked) blocked, $($result.skipped) untouched. The worker picks them up normally; re-running cannot duplicate Kusto output."
        return $result
    }
    default {
        $queryString = Get-DiagnosticsQuery -Parameters $Query
        if ($globalDiagnostics.Contains($Action)) {
            return Invoke-KoLiteApi -Method 'GET' -RelativeUri "$($globalDiagnostics[$Action])$queryString"
        }
        if ($perJobDiagnostics.Contains($Action)) {
            if ([string]::IsNullOrWhiteSpace($JobId)) {
                throw "$Action requires -JobId (the permanent GUID or activityId)."
            }
            $encoded = [uri]::EscapeDataString($JobId)
            return Invoke-KoLiteApi -Method 'GET' -RelativeUri "/api/jobs/$encoded/$($perJobDiagnostics[$Action])$queryString"
        }
        throw "Unhandled action '$Action'."
    }
}
