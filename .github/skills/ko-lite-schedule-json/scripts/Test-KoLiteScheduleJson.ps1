#requires -Version 7.0
<#
.SYNOPSIS
Validates KO Lite job-schedule JSON locally against the strict KO Lite
schedule contract.

.DESCRIPTION
Mirrors the rules enforced by KoLite.Local.Core's ScheduleParser
(src/KoLite.Local.Core/Schedules/ScheduleParser.cs). Does NOT contact
Kusto, does NOT upload, does NOT touch any catalog. Pure JSON validation.

Accepts either:
- a single JSON object describing one job, or
- a JSON array of such objects (the shape that KoLite.CatalogTools' import
  command accepts for upsert-only batches).

For each definition the validator returns a result object with:
  IsValid     [bool]
  ActivityId  [string] or $null when the field is missing/invalid
  Index       [int]    array index (0 for a single-object document)
  Errors      [string[]]

By default a human-readable summary is printed and the script exits with
code 1 if any definition is invalid. Use -Quiet to suppress output and
-FailOnError:$false to keep exit code 0 even on validation failures (useful
when consuming the returned objects in PowerShell).

.PARAMETER Path
Path to a JSON file to validate. Mutually exclusive with -Json.

.PARAMETER Json
Raw JSON string to validate. Mutually exclusive with -Path.

.PARAMETER Quiet
Suppress human-readable summary output. Result objects are still emitted.

.PARAMETER FailOnError
When $true (default), exit with code 1 if any definition is invalid.

.EXAMPLE
.\Test-KoLiteScheduleJson.ps1 -Path .\my-job.json

.EXAMPLE
Get-Content .\jobs.json -Raw | .\Test-KoLiteScheduleJson.ps1

.EXAMPLE
$results = .\Test-KoLiteScheduleJson.ps1 -Path .\jobs.json -Quiet -FailOnError:$false
$results | Where-Object { -not $_.IsValid }
#>
[CmdletBinding(DefaultParameterSetName = 'Path')]
param(
    [Parameter(ParameterSetName = 'Path', Mandatory = $true, Position = 0)]
    [string] $Path,

    [Parameter(ParameterSetName = 'Json', Mandatory = $true, ValueFromPipeline = $true)]
    [string] $Json,

    [switch] $Quiet,

    [bool] $FailOnError = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AllowedTopLevel = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
@(
    'activityId','functionName','outputTable','queryWindowSize',
    'delayFromUtcNow','maxParallelism','queryTimeout','isPaused',
    'startFrom','endOn','folder','tags','dependsOn','jobSettings','target'
) | ForEach-Object { [void]$AllowedTopLevel.Add($_) }

$AllowedTargetFields = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
@('clusterUri','database') | ForEach-Object { [void]$AllowedTargetFields.Add($_) }

$AllowedDependencyFields = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
[void]$AllowedDependencyFields.Add('activityId')

$StartFromRegex = [regex]::new(
    '^(?<dt>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)(?<off>Z|[+-]\d{2}:\d{2})?$',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

$Culture = [System.Globalization.CultureInfo]::InvariantCulture

function Get-Property {
    param(
        [Parameter(Mandatory)] [System.Text.Json.JsonElement] $Element,
        [Parameter(Mandatory)] [string] $Name
    )
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        return $null
    }
    foreach ($prop in $Element.EnumerateObject()) {
        if ($prop.NameEquals($Name)) {
            return ,$prop.Value
        }
    }
    return $null
}

function Test-TimeSpanString {
    param([string] $Value, [ref] $Parsed)
    $ts = [TimeSpan]::Zero
    if ([TimeSpan]::TryParseExact($Value, 'c', $Culture, [ref]$ts)) {
        $Parsed.Value = $ts
        return $true
    }
    return $false
}

function Add-Error {
    param([System.Collections.Generic.List[string]] $Errors, [string] $Field, [string] $Message)
    [void]$Errors.Add("[$Field] $Message")
}

function Test-UtcIso8601 {
    param(
        [Parameter(Mandatory)] [System.Text.Json.JsonElement] $Root,
        [Parameter(Mandatory)] [string] $FieldName,
        [Parameter(Mandatory)] [bool] $Required,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]] $Errors,
        [Parameter(Mandatory)] [ref] $ParsedUtc
    )
    $ParsedUtc.Value = $null
    $prop = Get-Property -Element $Root -Name $FieldName
    if ($null -eq $prop) {
        if ($Required) {
            Add-Error $Errors $FieldName "$FieldName is required."
        }
        return
    }
    if ($prop.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
        Add-Error $Errors $FieldName "$FieldName must be a string (got $($prop.ValueKind))."
        return
    }
    $raw = $prop.GetString()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Add-Error $Errors $FieldName "$FieldName must be a non-empty ISO-8601 string."
        return
    }
    $match = $StartFromRegex.Match($raw)
    if (-not $match.Success) {
        Add-Error $Errors $FieldName "$FieldName must be ISO-8601 of the form yyyy-MM-ddTHH:mm:ss[.fffffff][Z|+00:00]; got '$raw'."
        return
    }
    $offset = if ($match.Groups['off'].Success) { $match.Groups['off'].Value } else { $null }
    if ($null -ne $offset -and $offset -ne 'Z' -and $offset -ne '+00:00' -and $offset -ne '-00:00') {
        Add-Error $Errors $FieldName "$FieldName must be UTC (no offset, 'Z', or +00:00); got '$raw'."
        return
    }
    $datePart = $match.Groups['dt'].Value
    [string[]] $formats = @(
        'yyyy-MM-ddTHH:mm:ss',
        'yyyy-MM-ddTHH:mm:ss.f',
        'yyyy-MM-ddTHH:mm:ss.ff',
        'yyyy-MM-ddTHH:mm:ss.fff',
        'yyyy-MM-ddTHH:mm:ss.ffff',
        'yyyy-MM-ddTHH:mm:ss.fffff',
        'yyyy-MM-ddTHH:mm:ss.ffffff',
        'yyyy-MM-ddTHH:mm:ss.fffffff'
    )
    $dt = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact($datePart, $formats, $Culture, [System.Globalization.DateTimeStyles]::None, [ref]$dt)) {
        Add-Error $Errors $FieldName "$FieldName is not a valid date/time value: '$raw'."
        return
    }
    $ParsedUtc.Value = [DateTime]::SpecifyKind($dt, [DateTimeKind]::Utc)
}

function Test-Definition {
    param(
        [Parameter(Mandatory)] [System.Text.Json.JsonElement] $Root,
        [Parameter(Mandatory)] [int] $Index
    )

    $errors = [System.Collections.Generic.List[string]]::new()
    $activityId = $null

    if ($Root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        Add-Error $errors '<root>' "Schedule definition must be a JSON object (got $($Root.ValueKind))."
        return [pscustomobject]@{
            IsValid    = $false
            ActivityId = $null
            Index      = $Index
            Errors     = $errors.ToArray()
        }
    }

    foreach ($prop in $Root.EnumerateObject()) {
        if (-not $AllowedTopLevel.Contains($prop.Name)) {
            Add-Error $errors $prop.Name "Field '$($prop.Name)' is not part of the supported KO Lite schedule contract."
        }
    }

    $activityIdProp = Get-Property -Element $Root -Name 'activityId'
    if ($null -ne $activityIdProp) {
        if ($activityIdProp.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
            $raw = $activityIdProp.GetString()
            if (-not [string]::IsNullOrWhiteSpace($raw)) {
                $activityId = $raw
            }
            else {
                Add-Error $errors 'activityId' 'activityId is required and must be a non-empty string.'
            }
        }
        else {
            Add-Error $errors 'activityId' "activityId must be a string (got $($activityIdProp.ValueKind))."
        }
    }
    else {
        Add-Error $errors 'activityId' 'activityId is required and must be a non-empty string.'
    }

    function Read-RequiredString {
        param([string] $Name)
        $prop = Get-Property -Element $Root -Name $Name
        if ($null -eq $prop) {
            Add-Error $errors $Name "$Name is required and must be a non-empty string."
            return $null
        }
        if ($prop.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            Add-Error $errors $Name "$Name must be a string (got $($prop.ValueKind))."
            return $null
        }
        $value = $prop.GetString()
        if ([string]::IsNullOrWhiteSpace($value)) {
            Add-Error $errors $Name "$Name is required and must be a non-empty string."
            return $null
        }
        return $value
    }

    [void](Read-RequiredString -Name 'functionName')
    [void](Read-RequiredString -Name 'outputTable')

    function Read-RequiredTimeSpan {
        param([string] $Name, [ValidateSet('Positive','NonNegative')] [string] $Range)
        $prop = Get-Property -Element $Root -Name $Name
        if ($null -eq $prop) {
            Add-Error $errors $Name "$Name is required."
            return $null
        }
        if ($prop.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            Add-Error $errors $Name "$Name must be a TimeSpan string in 'c' format such as '00:15:00' (got $($prop.ValueKind))."
            return $null
        }
        $raw = $prop.GetString()
        $parsed = [TimeSpan]::Zero
        if (-not (Test-TimeSpanString -Value $raw -Parsed ([ref]$parsed))) {
            Add-Error $errors $Name "$Name must be a TimeSpan string in 'c' format ([d.]hh:mm:ss[.fffffff]); got '$raw'."
            return $null
        }
        if ($Range -eq 'Positive' -and $parsed -le [TimeSpan]::Zero) {
            Add-Error $errors $Name "$Name must be strictly greater than zero (got '$raw')."
        }
        elseif ($Range -eq 'NonNegative' -and $parsed -lt [TimeSpan]::Zero) {
            Add-Error $errors $Name "$Name must be greater than or equal to zero (got '$raw')."
        }
        return $parsed
    }

    [void](Read-RequiredTimeSpan -Name 'queryWindowSize' -Range Positive)
    [void](Read-RequiredTimeSpan -Name 'delayFromUtcNow' -Range NonNegative)
    [void](Read-RequiredTimeSpan -Name 'queryTimeout'    -Range Positive)

    $maxParProp = Get-Property -Element $Root -Name 'maxParallelism'
    if ($null -eq $maxParProp) {
        Add-Error $errors 'maxParallelism' 'maxParallelism is required.'
    }
    elseif ($maxParProp.ValueKind -ne [System.Text.Json.JsonValueKind]::Number) {
        Add-Error $errors 'maxParallelism' "maxParallelism must be an integer (got $($maxParProp.ValueKind))."
    }
    else {
        $mp = 0
        if (-not $maxParProp.TryGetInt32([ref]$mp)) {
            Add-Error $errors 'maxParallelism' 'maxParallelism must be a 32-bit integer.'
        }
        elseif ($mp -lt 1) {
            Add-Error $errors 'maxParallelism' "maxParallelism must be at least 1 (got $mp)."
        }
    }

    $isPausedProp = Get-Property -Element $Root -Name 'isPaused'
    if ($null -ne $isPausedProp) {
        $kind = $isPausedProp.ValueKind
        if ($kind -ne [System.Text.Json.JsonValueKind]::True -and $kind -ne [System.Text.Json.JsonValueKind]::False) {
            Add-Error $errors 'isPaused' "isPaused must be a boolean (got $kind)."
        }
    }

    $startFromUtc = $null
    Test-UtcIso8601 -Root $Root -FieldName 'startFrom' -Required $true -Errors $errors -ParsedUtc ([ref]$startFromUtc)

    $endOnUtc = $null
    Test-UtcIso8601 -Root $Root -FieldName 'endOn' -Required $false -Errors $errors -ParsedUtc ([ref]$endOnUtc)

    if ($null -ne $startFromUtc -and $null -ne $endOnUtc -and $endOnUtc -le $startFromUtc) {
        $startFmt = $startFromUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ', $Culture)
        $endFmt = $endOnUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ', $Culture)
        Add-Error $errors 'endOn' "endOn must be strictly greater than startFrom ('$startFmt'); got '$endFmt'."
    }

    $folderProp = Get-Property -Element $Root -Name 'folder'
    if ($null -ne $folderProp -and $folderProp.ValueKind -ne [System.Text.Json.JsonValueKind]::String -and $folderProp.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
        Add-Error $errors 'folder' "folder must be a string when present (got $($folderProp.ValueKind))."
    }

    $tagsProp = Get-Property -Element $Root -Name 'tags'
    if ($null -ne $tagsProp) {
        if ($tagsProp.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            Add-Error $errors 'tags' 'tags must be an array of strings.'
        }
        else {
            $i = 0
            foreach ($entry in $tagsProp.EnumerateArray()) {
                $path = "tags[$i]"
                if ($entry.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                    Add-Error $errors $path "$path must be a string."
                }
                elseif ([string]::IsNullOrWhiteSpace($entry.GetString())) {
                    Add-Error $errors $path "$path must be a non-empty string."
                }
                $i++
            }
        }
    }

    $targetProp = Get-Property -Element $Root -Name 'target'
    if ($null -eq $targetProp) {
        Add-Error $errors 'target' 'target is required.'
    }
    elseif ($targetProp.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        Add-Error $errors 'target' "target must be a JSON object (got $($targetProp.ValueKind))."
    }
    else {
        foreach ($prop in $targetProp.EnumerateObject()) {
            if (-not $AllowedTargetFields.Contains($prop.Name)) {
                Add-Error $errors "target.$($prop.Name)" "Field 'target.$($prop.Name)' is not part of the supported KO Lite schedule target contract."
            }
        }

        $clusterProp = Get-Property -Element $targetProp -Name 'clusterUri'
        if ($null -eq $clusterProp) {
            Add-Error $errors 'target.clusterUri' 'target.clusterUri is required and must be a non-empty string.'
        }
        elseif ($clusterProp.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            Add-Error $errors 'target.clusterUri' "target.clusterUri must be a string (got $($clusterProp.ValueKind))."
        }
        else {
            $uriString = $clusterProp.GetString()
            if ([string]::IsNullOrWhiteSpace($uriString)) {
                Add-Error $errors 'target.clusterUri' 'target.clusterUri is required and must be a non-empty string.'
            }
            else {
                $uri = $null
                if (-not [Uri]::TryCreate($uriString, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
                    Add-Error $errors 'target.clusterUri' "target.clusterUri must be an absolute https URI (got '$uriString')."
                }
            }
        }

        $dbProp = Get-Property -Element $targetProp -Name 'database'
        if ($null -eq $dbProp) {
            Add-Error $errors 'target.database' 'target.database is required and must be a non-empty string.'
        }
        elseif ($dbProp.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            Add-Error $errors 'target.database' "target.database must be a string (got $($dbProp.ValueKind))."
        }
        elseif ([string]::IsNullOrWhiteSpace($dbProp.GetString())) {
            Add-Error $errors 'target.database' 'target.database is required and must be a non-empty string.'
        }
    }

    $dependsOnProp = Get-Property -Element $Root -Name 'dependsOn'
    if ($null -ne $dependsOnProp -and $dependsOnProp.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
        if ($dependsOnProp.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            Add-Error $errors 'dependsOn' 'dependsOn must be an array of dependency objects.'
        }
        else {
            $i = 0
            foreach ($entry in $dependsOnProp.EnumerateArray()) {
                $path = "dependsOn[$i]"
                if ($entry.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                    Add-Error $errors $path "$path must be an object of shape { ""activityId"": ""..."" }; bare-string dependency shorthand is not supported."
                }
                elseif ($entry.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                    Add-Error $errors $path "$path must be a JSON object."
                }
                else {
                    foreach ($prop in $entry.EnumerateObject()) {
                        if (-not $AllowedDependencyFields.Contains($prop.Name)) {
                            Add-Error $errors "$path.$($prop.Name)" "Field '$path.$($prop.Name)' is not part of the supported KO Lite dependency contract."
                        }
                    }
                    $depIdProp = Get-Property -Element $entry -Name 'activityId'
                    $depId = $null
                    if ($null -eq $depIdProp) {
                        Add-Error $errors "$path.activityId" "$path.activityId is required and must be a non-empty string."
                    }
                    elseif ($depIdProp.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                        Add-Error $errors "$path.activityId" "$path.activityId must be a string."
                    }
                    else {
                        $depId = $depIdProp.GetString()
                        if ([string]::IsNullOrWhiteSpace($depId)) {
                            Add-Error $errors "$path.activityId" "$path.activityId is required and must be a non-empty string."
                        }
                        elseif ($null -ne $activityId -and [string]::Equals($depId, $activityId, [System.StringComparison]::Ordinal)) {
                            Add-Error $errors $path "$path declares self-dependency on '$activityId'."
                        }
                    }
                }
                $i++
            }
        }
    }

    return [pscustomobject]@{
        IsValid    = ($errors.Count -eq 0)
        ActivityId = $activityId
        Index      = $Index
        Errors     = $errors.ToArray()
    }
}

function Read-JsonText {
    if ($PSCmdlet.ParameterSetName -eq 'Path') {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            throw "JSON file not found: $Path"
        }
        return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).Path)
    }
    return $Json
}

$jsonText = Read-JsonText

try {
    $document = [System.Text.Json.JsonDocument]::Parse($jsonText)
}
catch {
    $msg = $_.Exception.Message
    $result = [pscustomobject]@{
        IsValid    = $false
        ActivityId = $null
        Index      = 0
        Errors     = @("[<json>] Schedule document is not valid JSON: $msg")
    }
    if (-not $Quiet) {
        Write-Host "FAIL  <invalid JSON>: $msg" -ForegroundColor Red
    }
    Write-Output $result
    if ($FailOnError) { exit 1 } else { exit 0 }
}

try {
    $root = $document.RootElement
    $results = [System.Collections.Generic.List[object]]::new()

    if ($root.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        $idx = 0
        foreach ($element in $root.EnumerateArray()) {
            $results.Add((Test-Definition -Root $element -Index $idx))
            $idx++
        }
        if ($results.Count -eq 0) {
            $results.Add([pscustomobject]@{
                IsValid    = $false
                ActivityId = $null
                Index      = 0
                Errors     = @('[<root>] Schedule array is empty; expected at least one job definition.')
            })
        }
    }
    elseif ($root.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $results.Add((Test-Definition -Root $root -Index 0))
    }
    else {
        $results.Add([pscustomobject]@{
            IsValid    = $false
            ActivityId = $null
            Index      = 0
            Errors     = @("[<root>] Schedule document must be a JSON object or an array of objects (got $($root.ValueKind)).")
        })
    }

    if (-not $Quiet) {
        $passCount = @($results | Where-Object { $_.IsValid }).Count
        $failCount = $results.Count - $passCount
        foreach ($r in $results) {
            $label = if ($null -ne $r.ActivityId) { $r.ActivityId } else { "<index $($r.Index)>" }
            if ($r.IsValid) {
                Write-Host "PASS  $label" -ForegroundColor Green
            }
            else {
                Write-Host "FAIL  $label" -ForegroundColor Red
                foreach ($err in $r.Errors) {
                    Write-Host "        $err" -ForegroundColor Red
                }
            }
        }
        Write-Host ""
        Write-Host "Validated $($results.Count) definition(s): $passCount passed, $failCount failed."
    }

    Write-Output $results

    $anyFailed = @($results | Where-Object { -not $_.IsValid }).Count -gt 0
    if ($anyFailed -and $FailOnError) {
        exit 1
    }
    exit 0
}
finally {
    $document.Dispose()
}
