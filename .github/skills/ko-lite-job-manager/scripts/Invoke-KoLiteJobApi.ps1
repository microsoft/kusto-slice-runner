#Requires -Version 7.0
<#
.SYNOPSIS
    Reads jobs and creates/updates schedules in a running KO Lite app through its
    localhost JSON API.

.DESCRIPTION
    Thin, rigorous wrapper around the KO Lite local management API
    (see docs\local-api.md). Every write goes through POST /api/jobs/import, which
    is the same validated, additive/update-only catalog path the dashboard import
    uses. This script intentionally exposes no enable/disable, delete, Kusto,
    rerun, or repair surface - it only reads and upserts schedules.

    Before importing, the body is validated locally with the sibling
    ko-lite-schedule-json validator unless -SkipValidation is supplied.

.PARAMETER Action
    Health     GET  /status/health  - confirm the app is up and print databasePath.
    Get-Jobs   GET  /api/jobs        - list job summaries.
    Get-Job    GET  /api/jobs/{id}   - one job summary plus its canonical schedule.
    Export     GET  /api/jobs/export - import-compatible array of all active jobs.
    Import     POST /api/jobs/import - create/update schedules (single object or array).

.PARAMETER BaseUrl
    KO Lite base URL. Defaults to the loopback default http://127.0.0.1:5057.

.PARAMETER JobId
    Job id (activityId) for Get-Job.

.PARAMETER Json
    Schedule JSON string (single object or array) for Import.

.PARAMETER Path
    Path to a schedule JSON file for Import (alternative to -Json).

.PARAMETER SkipValidation
    Skip the local schedule-JSON validation step before Import. Not recommended.

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Health

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Get-Jobs

.EXAMPLE
    .\Invoke-KoLiteJobApi.ps1 -Action Import -Path .\my-job.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('Health', 'Get-Jobs', 'Get-Job', 'Export', 'Import')]
    [string] $Action,

    [string] $BaseUrl = 'http://127.0.0.1:5057',

    [string] $JobId,

    [string] $Json,

    [string] $Path,

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
            throw 'Get-Job requires -JobId.'
        }
        $encoded = [uri]::EscapeDataString($JobId)
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
}
