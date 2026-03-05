#Requires -Version 5.1
# Quick dev helper: kill any running service and restart it from the Release build.
# For a full build + deploy, use build-all.ps1 and deploy.ps1 instead.

$root       = $PSScriptRoot
$exe        = "$root\src\SAGIDE.Service\bin\Release\net9.0\SAGIDE.Service.exe"
$cwd        = "$root\src\SAGIDE.Service\bin\Release\net9.0"
$prompts    = "$root\prompts"
$skills     = "$root\skills"

if (-not (Test-Path $exe)) {
    Write-Error "Service EXE not found. Run: .\build-all.ps1"
    exit 1
}

# Kill any running instance
Get-Process -Name "SAGIDE.Service" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Write-Output "Old process stopped"

# Start fresh
Start-Process -FilePath $exe `
    -WorkingDirectory $cwd `
    -ArgumentList "--SAGIDE:PromptsPath=$prompts", "--SAGIDE:SkillsPath=$skills" `
    -WindowStyle Normal
Write-Output "SAGIDE Service started"
