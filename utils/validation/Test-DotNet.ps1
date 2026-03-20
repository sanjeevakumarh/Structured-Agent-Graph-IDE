[CmdletBinding()]
param()

function Test-DotNet {
    $result = [pscustomobject]@{ Name = "DotNet SDK"; Passed = $false; Details = "" }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $cmd) {
        $result.Details = "dotnet not on PATH"
        return $result
    }

    try {
        $versionText = dotnet --version
    } catch {
        $result.Details = "dotnet --version failed: $_"
        return $result
    }

    $min = [version]"9.0.0"
    $parsed = $null
    if (-not [version]::TryParse($versionText, [ref]$parsed)) {
        $result.Details = "cannot parse dotnet version: $versionText"
        return $result
    }
    if ($parsed -lt $min) {
        $result.Details = "dotnet $parsed < $min"
        return $result
    }

    try {
        dotnet workload list | Out-Null
    } catch {
        $result.Details = "dotnet workload list failed: $_"
        return $result
    }

    $result.Passed = $true
    $result.Details = "dotnet $parsed ok"
    return $result
}
