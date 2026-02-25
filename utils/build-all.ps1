param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot  = Split-Path -Parent $scriptDir
Push-Location $repoRoot

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host "`n==> $Name"
    & $Action
}

try {
    Invoke-Step "Build .NET solution (core + service)" {
        dotnet build "src/SAGIDE.sln" -c $Configuration --nologo
    }

    Invoke-Step "Build CLI (tools/cli/sag)" {
        dotnet build "tools/cli/sag/sag.csproj" -c $Configuration --nologo
    }

    Invoke-Step "Build VS Code extension" {
        # npm install (not npm ci) — package-lock.json is git-ignored
        npm install --prefix "src/vscode-extension" --no-audit --no-fund
        # compile runs tsc via the locally installed typescript in node_modules
        npm run compile --prefix "src/vscode-extension"
    }

    Invoke-Step "Build Logseq plugin" {
        npm install --prefix "tools/logseq-plugin" --no-audit --no-fund
        npm run build --prefix "tools/logseq-plugin"
    }
}
finally {
    Pop-Location
}
