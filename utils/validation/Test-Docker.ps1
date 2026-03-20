[CmdletBinding()]
param()

function Test-Docker {
    $result = [pscustomobject]@{ Name = "Docker"; Passed = $false; Details = "" }
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if (-not $docker) {
        $result.Details = "docker not on PATH"
        return $result
    }

    try { docker info --format '{{.ServerVersion}}' | Out-Null } catch { $result.Details = "docker info failed: $_"; return $result }
    try { docker ps | Out-Null } catch { $result.Details = "docker ps failed: $_"; return $result }
    try { docker compose version | Out-Null } catch { $result.Details = "docker compose version failed: $_"; return $result }
    try { docker buildx version | Out-Null } catch { $result.Details = "docker buildx version failed: $_"; return $result }

    $result.Passed = $true
    $result.Details = "docker engine reachable; compose and buildx available"
    return $result
}
