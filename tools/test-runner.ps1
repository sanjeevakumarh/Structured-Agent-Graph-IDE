<#
.SYNOPSIS
    Batch workflow test runner with LLM critique for prompts and skills.

.DESCRIPTION
    Reads a text file of workflow inputs separated by "------" lines.
    Supports two modes controlled by the -Type parameter:

    Type = "prompt" (default):
      1. POSTs to /api/prompts/{domain}/{name}/run with the input variables
      2. Polls for completion (up to $TimeoutSeconds)
      3. Reads the generated report and trace from known output paths
      4. Sends report + trace summary to an Ollama model for critique
      5. Stores the critique in ~/reports/critiques/{domain}/{name}/

    Type = "skill":
      1. Parses input blocks with "# parameters" and "# variables" sections
      2. POSTs to /api/skills/{domain}/{name}/run (synchronous, no polling)
      3. Sends the returned output to an Ollama model for critique
      4. Stores the critique in ~/reports/critiques/{domain}/{name}/

.PARAMETER InputFile
    Path to a text file. Blocks are separated by lines that start with "------".

    For prompts (-Type prompt), each block is a simple key: value list:
        ticker: MSFT
        context: dividend-focused investor
    ------
        ticker: AAPL
        context: growth investor

    For skills (-Type skill), each block may have "# parameters" and
    "# variables" section headers:
        # parameters
        ticker: MSFT
        asset_type: stock
        # variables
        idea: some idea
    ------
        # parameters
        ticker: AAPL

.PARAMETER Domain
    Prompt or skill domain (e.g. finance, research).

.PARAMETER Name
    Prompt or skill name (e.g. stock-analysis, fundamental-analyst).

.PARAMETER Type
    Whether to test a "prompt" or a "skill". Default: prompt.

.PARAMETER ServiceUrl
    Base URL of the SAGIDE service. Default: http://localhost:5100

.PARAMETER CritiqueServer
    Ollama server URL for critique calls. Default: http://localhost:11434

.PARAMETER CritiqueModel
    Ollama model to use for critique. Default: qwen2.5:14b-instruct-q5_K_M

.PARAMETER TimeoutSeconds
    Max seconds to wait for each workflow run (prompts only). Default: 600

.PARAMETER PollIntervalSeconds
    Seconds between status polls (prompts only). Default: 10

.PARAMETER MaxRuns
    Max number of input blocks to process (0 = all). Useful for quick smoke tests.

.EXAMPLE
    .\test-runner.ps1 -InputFile ..\testData\finance.stock-analysis.txt -Domain finance -Name stock-analysis

.EXAMPLE
    .\test-runner.ps1 -InputFile ..\testData\research.idea-to-product-seq.txt -Domain research -Name idea-to-product-seq `
        -CritiqueModel llama3.1:8b -MaxRuns 3

.EXAMPLE
    .\test-runner.ps1 -InputFile ..\testData\finance.fundamental-analyst.txt -Domain finance -Name fundamental-analyst -Type skill
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $InputFile,

    [Parameter(Mandatory)]
    [string] $Domain,

    [Parameter(Mandatory)]
    [string] $Name,

    [ValidateSet("prompt", "skill")]
    [string] $Type              = "prompt",

    [string] $ServiceUrl        = "http://localhost:5100",
    [string] $CritiqueServer    = "http://localhost:11434",
    [string] $CritiqueModel     = "llama3.1:8b-instruct-q6_K",
    [int]    $TimeoutSeconds    = 600,
    [int]    $PollIntervalSeconds = 10,
    [int]    $MaxRuns           = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Helpers ───────────────────────────────────────────────────────────────────

function Write-Step($msg) { Write-Host "[>] $msg" -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }

function Parse-YamlBlock([string] $block) {
    # Minimal key: value parser — handles simple scalars and quoted strings.
    # For complex inputs, extend with a proper YAML parser.
    $vars = @{}
    foreach ($line in $block -split "`n") {
        $line = $line.Trim()
        if ($line -eq "" -or $line.StartsWith("#")) { continue }
        $colon = $line.IndexOf(":")
        if ($colon -lt 0) { continue }
        $key   = $line.Substring(0, $colon).Trim()
        $value = $line.Substring($colon + 1).Trim().Trim('"').Trim("'")
        $vars[$key] = $value
    }
    return $vars
}

function Parse-SkillBlock([string] $block) {
    $parameters = @{}
    $variables = @{}
    $section = "parameters"  # default section
    foreach ($line in $block -split "`n") {
        $line = $line.Trim()
        if ($line -eq "" -or ($line.StartsWith("#") -and $line -ne "# parameters" -and $line -ne "# variables")) { continue }
        if ($line -eq "# parameters") { $section = "parameters"; continue }
        if ($line -eq "# variables")  { $section = "variables"; continue }
        $colon = $line.IndexOf(":")
        if ($colon -lt 0) { continue }
        $key   = $line.Substring(0, $colon).Trim()
        $value = $line.Substring($colon + 1).Trim().Trim('"').Trim("'")
        if ($section -eq "parameters") { $parameters[$key] = $value }
        else                           { $variables[$key]  = $value }
    }
    return @{ Parameters = $parameters; Variables = $variables }
}

function Invoke-ServicePost([string] $url, [object] $body) {
    $json = $body | ConvertTo-Json -Depth 10
    return Invoke-RestMethod -Uri $url -Method POST `
        -ContentType "application/json" -Body $json
}

function Poll-RunResult([string] $runId, [int] $timeout, [int] $interval) {
    $deadline = (Get-Date).AddSeconds($timeout)
    $statusUrl = "$ServiceUrl/api/prompts/$Domain/$Name/runs/$runId"

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $interval
        $resp = Invoke-RestMethod -Uri $statusUrl -Method GET -ErrorAction SilentlyContinue
        if ($null -eq $resp) { continue }

        $status = $resp.status ?? $resp.Status
        Write-Verbose "  poll $runId status=$status"

        if ($status -in @("completed", "failed", "aborted")) {
            return $resp
        }
    }
    return $null  # timed out
}

function Read-LatestReport([string] $runId, [hashtable] $vars) {
    # Reports path pattern from the prompt output destination.
    # The service writes reports to ~/reports/{output_subdir}/{datestamp}-{slug}.md
    # output_subdir comes from the input variables, falling back to $Domain.
    $outputSubdir = if ($vars -and $vars.ContainsKey("output_subdir") -and $vars["output_subdir"]) { $vars["output_subdir"] } else { $Domain }
    $reportsRoot = Join-Path $HOME "reports\$outputSubdir"
    if (-not (Test-Path $reportsRoot)) { return $null }

    $cutoff = (Get-Date).AddSeconds(-$TimeoutSeconds - 60)
    $files  = @(Get-ChildItem $reportsRoot -Filter "*.md" -ErrorAction SilentlyContinue |
              Where-Object { $_.LastWriteTime -gt $cutoff } |
              Sort-Object LastWriteTime -Descending)

    if ($files.Count -eq 0) { return $null }
    return @{ Path = $files[0].FullName; Content = Get-Content $files[0].FullName -Raw }
}

function Read-TraceFiles([string] $reportPath) {
    if ($null -eq $reportPath) { return "" }
    $traceDir = [System.IO.Path]::ChangeExtension($reportPath, $null)  # strip .md
    if (-not (Test-Path $traceDir)) { return "" }

    $summary = @()
    Get-ChildItem $traceDir -Recurse -Include "*.json","*.txt" |
        Sort-Object Name |
        ForEach-Object {
            $content = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
            if ($content) {
                $summary += "=== $($_.Name) ===`n$($content.Substring(0, [Math]::Min(500, $content.Length)))`n"
            }
        }
    return $summary -join "`n"
}

function Invoke-Critique([string] $runLabel, [string] $reportContent, [string] $traceContent) {
    $prompt = @"
You are an expert AI workflow quality auditor. Review the following SAGIDE workflow run output.

RUN LABEL: $runLabel
WORKFLOW: $Domain/$Name

---
REPORT CONTENT (first 3000 chars):
$($reportContent.Substring(0, [Math]::Min(3000, $reportContent.Length)))

---
TRACE SUMMARY (first 2000 chars):
$($traceContent.Substring(0, [Math]::Min(2000, $traceContent.Length)))

---
Provide a structured critique:

## DATA QUALITY (0-10)
Score how well the report cites verifiable sources vs. fabricating numbers.
List any specific fabrications or unsupported claims you can identify.

## COMPLETENESS (0-10)
Were all expected sections present? Were any required outputs missing?

## ACCURACY SIGNALS
Flag any statements that appear factually incorrect or inconsistent with the trace data.
Write "[CANNOT VERIFY]" if you cannot determine accuracy from the report alone.

## FAIL-LOUD COMPLIANCE
Did the report use [DATA UNAVAILABLE], [DATA MISSING], or similar markers when data was absent?
Or did it fill gaps silently?

## TOP 3 IMPROVEMENT RECOMMENDATIONS
Specific, actionable changes to the prompt or skill YAML to improve quality.

## OVERALL SCORE (0-100)
"@

    $body = @{
        model  = $CritiqueModel
        prompt = $prompt
        stream = $false
    }

    try {
        $resp = Invoke-RestMethod -Uri "$CritiqueServer/api/generate" `
            -Method POST -ContentType "application/json" `
            -Body ($body | ConvertTo-Json -Depth 5)
        return $resp.response ?? ""
    }
    catch {
        Write-Warn "Critique call failed: $_"
        return "[CRITIQUE FAILED: $_]"
    }
}

# ── Main ──────────────────────────────────────────────────────────────────────

if (-not (Test-Path $InputFile)) {
    Write-Fail "Input file not found: $InputFile"
    exit 1
}

$rawContent = Get-Content $InputFile -Raw
$blocks     = @($rawContent -split "(?m)^------+\s*\r?$" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })

if ($blocks.Count -eq 0) {
    Write-Fail "No input blocks found in $InputFile (use ------ to separate blocks)"
    exit 1
}

if ($MaxRuns -gt 0 -and $MaxRuns -lt $blocks.Count) {
    Write-Warn "MaxRuns=$MaxRuns — processing first $MaxRuns of $($blocks.Count) blocks"
    $blocks = $blocks[0..($MaxRuns - 1)]
}

$critiqueDir = Join-Path $HOME "reports\critiques\$Domain\$Name"
New-Item -ItemType Directory -Force -Path $critiqueDir | Out-Null

$results     = @()
$blockIndex  = 0

foreach ($block in $blocks) {
    $blockIndex++

    if ($Type -eq "skill") {
        # ── Skill mode ─────────────────────────────────────────────────────────
        $parsed   = Parse-SkillBlock $block
        $labelVars = $parsed.Parameters
        $runLabel = "$Domain/$Name #$blockIndex"
        if ($labelVars.ContainsKey("ticker")) { $runLabel += " ($($labelVars['ticker']))" }
        elseif ($labelVars.ContainsKey("subject")) { $runLabel += " ($($labelVars['subject'].Substring(0, [Math]::Min(40, $labelVars['subject'].Length))))…" }

        Write-Step "Skill run $blockIndex/$($blocks.Count): $runLabel"

        $skillUrl = "$ServiceUrl/api/skills/$Domain/$Name/run"
        $skillBody = @{ parameters = $parsed.Parameters; variables = $parsed.Variables }

        # ── Submit (synchronous — no polling) ──────────────────────────────────
        $skillResp = $null
        try {
            $skillResp = Invoke-ServicePost $skillUrl $skillBody
            Write-OK "Skill returned (synchronous)"
        }
        catch {
            Write-Fail "Skill submit failed: $_"
            $results += [pscustomobject]@{
                Label  = $runLabel; Status = "SUBMIT_FAILED"; ReportPath = ""; CritiquePath = ""
            }
            continue
        }

        $finalStatus  = "completed"
        $reportContent = $skillResp.output ?? $skillResp.Output ?? "[no output]"
        $traceFolder   = $skillResp.trace_folder ?? $skillResp.TraceFolder ?? ""
        $traceSummary  = ""
        if ($traceFolder -ne "" -and (Test-Path $traceFolder)) {
            $traceSummary = Read-TraceFiles $traceFolder
        }

        Write-OK "Output length: $($reportContent.Length) chars"
        if ($traceFolder -ne "") { Write-OK "Trace: $traceFolder" }

        # ── Critique ───────────────────────────────────────────────────────────
        Write-Step "Sending to $CritiqueModel on $CritiqueServer for critique..."
        $critiqueText = Invoke-Critique $runLabel $reportContent $traceSummary

        # ── Store critique ─────────────────────────────────────────────────────
        $timestamp    = Get-Date -Format "yyyy-MM-dd-HH-mm-ss"
        $safeLabel    = ($runLabel -replace "[^a-zA-Z0-9_-]", "-") -replace "-+","-"
        $critiquePath = Join-Path $critiqueDir "$timestamp-$safeLabel.md"

        $critiqueDoc = @"
# Critique: $runLabel
**Date**: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
**Workflow**: $Domain/$Name (skill)
**Status**: $finalStatus
**Trace**: $traceFolder
**Critique model**: $CritiqueModel @ $CritiqueServer

## Input
``````yaml
$block
``````

## Skill Output (first 3000 chars)

$($reportContent.Substring(0, [Math]::Min(3000, $reportContent.Length)))

## LLM Critique

$critiqueText
"@

        Set-Content -Path $critiquePath -Value $critiqueDoc -Encoding UTF8
        Write-OK "Critique saved: $critiquePath"

        $results += [pscustomobject]@{
            Label        = $runLabel
            Status       = $finalStatus
            ReportPath   = $traceFolder
            CritiquePath = $critiquePath
        }
    }
    else {
        # ── Prompt mode (original behavior) ────────────────────────────────────
        $vars     = Parse-YamlBlock $block
        $runLabel = "$Domain/$Name #$blockIndex"
        if ($vars.ContainsKey("ticker")) { $runLabel += " ($($vars['ticker']))" }
        elseif ($vars.ContainsKey("idea")) { $runLabel += " ($($vars['idea'].Substring(0, [Math]::Min(40, $vars['idea'].Length))))…" }

        Write-Step "Run $blockIndex/$($blocks.Count): $runLabel"

        $runUrl = "$ServiceUrl/api/prompts/$Domain/$Name/run"

        # ── Submit ─────────────────────────────────────────────────────────────
        $submitTime = Get-Date
        try {
            $submitResp = Invoke-ServicePost $runUrl $vars
            $respStatus = if ($submitResp -is [hashtable] -or $submitResp.PSObject.Properties.Name -contains 'status') { $submitResp.status } else { "accepted" }
            Write-OK "Submitted — status=$respStatus"
        }
        catch {
            Write-Fail "Submit failed: $_"
            $results += [pscustomobject]@{
                Label  = $runLabel; Status = "SUBMIT_FAILED"; ReportPath = ""; CritiquePath = ""
            }
            continue
        }

        # ── Wait for report file ──────────────────────────────────────────────
        # The workflow runs async (fire-and-forget). We poll for a new report
        # file in ~/reports/{domain}/ that appeared after submission time.
        Write-Step "Waiting for report file (up to ${TimeoutSeconds}s)..."
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $report   = $null
        # Use output_subdir from the input variables if set, otherwise fall back to $Domain
        $outputSubdir = if ($vars.ContainsKey("output_subdir") -and $vars["output_subdir"]) { $vars["output_subdir"] } else { $Domain }
        $reportsRoot = Join-Path $HOME "reports\$outputSubdir"

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds $PollIntervalSeconds
            if (Test-Path $reportsRoot) {
                $candidates = @(Get-ChildItem $reportsRoot -Filter "*.md" -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTime -gt $submitTime } |
                    Sort-Object LastWriteTime -Descending)
                if ($candidates.Count -gt 0) {
                    $report = @{ Path = $candidates[0].FullName; Content = Get-Content $candidates[0].FullName -Raw }
                    break
                }
            }
            Write-Verbose "  waiting... $(((Get-Date) - $submitTime).TotalSeconds)s elapsed"
        }

        if ($null -eq $report) {
            Write-Warn "Timed out - no report file appeared in $reportsRoot"
            $results += [pscustomobject]@{
                Label  = $runLabel; Status = "TIMEOUT"; ReportPath = ""; CritiquePath = ""
            }
            continue
        }

        $finalStatus = "completed"
        Write-OK "Report: $($report.Path)"

        $traceSummary = Read-TraceFiles $report.Path

        # ── Critique ───────────────────────────────────────────────────────────
        Write-Step "Sending to $CritiqueModel on $CritiqueServer for critique..."
        $critiqueText = Invoke-Critique $runLabel $report.Content $traceSummary

        # ── Store critique ─────────────────────────────────────────────────────
        $timestamp    = Get-Date -Format "yyyy-MM-dd-HH-mm-ss"
        $safeLabel    = ($runLabel -replace "[^a-zA-Z0-9_-]", "-") -replace "-+","-"
        $critiquePath = Join-Path $critiqueDir "$timestamp-$safeLabel.md"

        $critiqueDoc = @"
# Critique: $runLabel
**Date**: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
**Workflow**: $Domain/$Name
**Status**: $finalStatus
**Report**: $($report.Path)
**Critique model**: $CritiqueModel @ $CritiqueServer

## Input Variables
``````yaml
$block
``````

## LLM Critique

$critiqueText
"@

        Set-Content -Path $critiquePath -Value $critiqueDoc -Encoding UTF8
        Write-OK "Critique saved: $critiquePath"

        $results += [pscustomobject]@{
            Label        = $runLabel
            Status       = $finalStatus
            ReportPath   = $report.Path
            CritiquePath = $critiquePath
        }
    }

    Write-Host ""
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host "`n=== BATCH SUMMARY ===" -ForegroundColor White
$results | Format-Table Label, Status, ReportPath -AutoSize

$summaryPath = Join-Path $critiqueDir "$(Get-Date -Format 'yyyy-MM-dd-HH-mm-ss')-batch-summary.md"
$summaryLines = @("# Batch Test Summary", "", "**Workflow**: $Domain/$Name", "**Date**: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", "**Input file**: $InputFile", "**Critique model**: $CritiqueModel @ $CritiqueServer", "")
$summaryLines += "| Run | Status | Report | Critique |"
$summaryLines += "|-----|--------|--------|---------|"
foreach ($r in $results) {
    $summaryLines += "| $($r.Label) | $($r.Status) | $($r.ReportPath) | $($r.CritiquePath) |"
}

Set-Content -Path $summaryPath -Value ($summaryLines -join "`n") -Encoding UTF8
Write-OK "Batch summary: $summaryPath"
