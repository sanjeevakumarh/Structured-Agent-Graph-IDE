[CmdletBinding()]
param()

function Test-Git {
    $result = [pscustomobject]@{ Name = "Git"; Passed = $false; Details = "" }
    $cmd = Get-Command git -ErrorAction SilentlyContinue
    if (-not $cmd) {
        $result.Details = "git not on PATH"
        return $result
    }
    try {
        $ver = git --version
    } catch {
        $result.Details = "git --version failed: $_"
        return $result
    }
    $result.Passed = $true
    $result.Details = $ver
    return $result
}
