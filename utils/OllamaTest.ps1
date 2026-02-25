# Benchmarks a coding model with 5 prompts, times each call, prints summary stats, and logs per-call details.
# Usage examples:
#   .\OllamaTest.ps1
#   .\OllamaTest.ps1 -HostName "server1" -Model "deepseek-coder-v2:16b"
#   .\OllamaTest.ps1 -HostName "server2" -Model "qwen2.5-coder:7b-instruct-q4_K_M"
#   .\OllamaTest.ps1 -HostName "localhost" -Model "phi4:14b-q4_k_m"


param(
    [string]$HostName = "localhost",
    [string]$Model = "qwen2.5-coder:7b-instruct"
)

# Log file setup
$logDir = "C:\Temp\Logs"
if (-not (Test-Path $logDir)) { New-Item -Path $logDir -ItemType Directory -Force | Out-Null }
$timestamp = (Get-Date).ToString("yyyy-MM-dd-HH-mm")
$sagHost = ($HostName -replace '[^\w\-\.]', "_")
$sagModel = ($Model -replace '[^\w\-\.]', "_")
$logPath = Join-Path $logDir "$sagHost-$sagModel-$timestamp.txt"

# Simple prompts to exercise and benchmark the model
$Prompts = @(
    'Write SQRT in C# using newton Raphson method',
    'Add unit tests for a Fibonacci function in Python (edge cases included)',
    'Review this Java snippet for thread safety: public void inc(){count++;}',
    'Design review: REST API with POST /login, POST /transfer, GET /balance - suggest improvements',
    'Security review this Node.js snippet for injection/secrets: const q = `select * from users where name = ''${name}''`; db.query(q);'
)

$results = @()

function Get-Median([double[]]$nums) {
    $sorted = $nums | Sort-Object
    $n = $sorted.Count
    if ($n -eq 0) { return $null }
    if ($n % 2 -eq 1) { return $sorted[[int]($n/2)] }
    return (($sorted[$n/2 - 1] + $sorted[$n/2]) / 2)
}

foreach ($Prompt in $Prompts) {
    $Body = @{
        model  = $Model
        prompt = $Prompt
        stream = $false
    } | ConvertTo-Json

    $start = Get-Date
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-RestMethod -Uri "http://$($HostName):11434/api/generate" `
        -Method Post `
        -Body $Body `
        -ContentType "application/json" `
        -TimeoutSec 0
    $sw.Stop()
    $end = Get-Date
    $elapsedSec = [math]::Round($sw.Elapsed.TotalSeconds, 3)

    # Record per-run timing
    $results += [pscustomobject]@{
        Prompt      = $Prompt
        DurationSec = $elapsedSec
    }

    # Emit response if you want to see it; comment out next line to silence.
    # $response

    # Log request/response timing details
    Add-Content -Path $logPath -Value @"
---
Prompt: $Prompt
Start:  $start
End:    $end
DurationSec: $elapsedSec
Response:
$($response | Out-String)
"@
}

Write-Host ""
Write-Host "Summary:"
foreach ($r in $results) {
    Write-Host "Prompt: $($r.Prompt) | Duration: $($r.DurationSec) s"
}

$durations = $results | Select-Object -ExpandProperty DurationSec
$avg = [math]::Round((($durations | Measure-Object -Average).Average), 3)
$median = [math]::Round((Get-Median $durations), 3)

Write-Host "Average: ${avg} s"
Write-Host "Median:  ${median} s"