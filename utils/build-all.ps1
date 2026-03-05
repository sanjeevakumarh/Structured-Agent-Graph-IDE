param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$BuildClients
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

    if ($BuildClients) {
        Invoke-Step "Build CLI (tools/cli/sag)" {
            dotnet build "tools/cli/sag/sag.csproj" -c $Configuration --nologo
        }
    }

    if ($BuildClients) {
        Invoke-Step "Build VS Code extension" {
            Push-Location "src/vscode-extension"
            try {
                # npm install (not npm ci) — package-lock.json is git-ignored
                npm install --no-audit --no-fund
                # compile runs tsc via the locally installed typescript in node_modules
                npm run compile
                # package the extension (.vsix) so downstream consumers find the artifact
                "y" | npx vsce package --no-dependencies --allow-star-activation --allow-missing-repository
            } finally {
                Pop-Location
            }
        }
    }

    if ($BuildClients) {
        Invoke-Step "Build Logseq plugin" {
            npm install --prefix "tools/logseq-plugin" --no-audit --no-fund
            npm run build --prefix "tools/logseq-plugin"
        }
    }
}
finally {
    Pop-Location
}
