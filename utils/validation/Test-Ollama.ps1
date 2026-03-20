[CmdletBinding()]
param()

function Test-Ollama {
    param(
        [string]$Host = "localhost",
        [int]$Port = 11434,
        [string]$Model = ""
    )

    $result = [pscustomobject]@{ Name = "Ollama"; Passed = $false; Details = "" }
    $url = "http://$Host`:$Port/api/tags"
    try {
        $resp = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 10
    } catch {
        $result.Details = "Ollama not reachable at $Host:$Port - $_"
        return $result
    }

    if ($Model) {
        $found = $false
        foreach ($m in $resp.models) {
            if ($m.name -eq $Model) { $found = $true; break }
        }
        if (-not $found) {
            $result.Details = "Ollama reachable but model '$Model' not present"
            return $result
        }
        $result.Details = "Ollama reachable; model '$Model' present"
    } else {
        $names = $resp.models.name -join ', '
        $result.Details = "Ollama reachable; models: $names"
    }

    $result.Passed = $true
    return $result
}
