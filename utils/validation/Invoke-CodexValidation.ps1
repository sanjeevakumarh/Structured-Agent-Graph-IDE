[CmdletBinding()]
param(
    [string]$OllamaHost = "localhost",
    [int]$OllamaPort = 11434,
    [string]$OllamaModel = "",
    [string[]]$SearxngEndpoints = @(),
    [switch]$SkipOllama,
    [switch]$SkipSearxng,
    [switch]$SkipPython,
    [switch]$SkipDocker,
    [switch]$SkipPaths,
    [string]$PythonCommand = "python"
)

$root = $PSScriptRoot
. "$root/Test-DotNet.ps1"
. "$root/Test-NodeNpm.ps1"
. "$root/Test-Docker.ps1"
. "$root/Test-Ollama.ps1"
. "$root/Test-Searxng.ps1"
. "$root/Test-Git.ps1"
. "$root/Test-Python.ps1"
. "$root/Test-Paths.ps1"

$results = @()
$results += Test-DotNet
$results += Test-NodeNpm
$results += Test-Git
if (-not $SkipDocker) { $results += Test-Docker }
if (-not $SkipPaths) { $results += Test-Paths }
if (-not $SkipPython) { $results += Test-Python -Command $PythonCommand }
if (-not $SkipOllama) { $results += Test-Ollama -Host $OllamaHost -Port $OllamaPort -Model $OllamaModel }
if (-not $SkipSearxng) { $results += Test-Searxng -Endpoints $SearxngEndpoints }

$flat = @()
foreach ($r in $results) { $flat += $r }

Write-Host "" 
Write-Host "Codex validation results" -ForegroundColor Cyan
foreach ($r in $flat) {
    $color = if ($r.Passed) { "Green" } else { "Red" }
    $status = if ($r.Passed) { "OK" } else { "FAIL" }
    Write-Host ("[{0}] {1} - {2}" -f $status, $r.Name, $r.Details) -ForegroundColor $color
}

$failed = $flat | Where-Object { -not $_.Passed }
$passed = $flat | Where-Object { $_.Passed }
Write-Host "" 
Write-Host ("Passed: {0}  Failed: {1}" -f $passed.Count, $failed.Count)
if ($failed.Count -gt 0) { exit 1 }
