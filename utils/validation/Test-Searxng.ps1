[CmdletBinding()]
param()

function Test-Searxng {
    param([string[]]$Endpoints = @())

    $results = @()
    if (-not $Endpoints -or $Endpoints.Count -eq 0) {
        $results += [pscustomobject]@{ Name = "Searxng"; Passed = $true; Details = "No endpoints provided; skipped" }
        return $results
    }

    foreach ($url in $Endpoints) {
        $item = [pscustomobject]@{ Name = "Searxng $url"; Passed = $false; Details = "" }
        try {
            $resp = Invoke-WebRequest -Uri $url -Method Get -TimeoutSec 10
            if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300) {
                $item.Passed = $true
                $item.Details = "HTTP $($resp.StatusCode)"
            } else {
                $item.Details = "HTTP $($resp.StatusCode)"
            }
        } catch {
            $item.Details = "Failed to reach $url: $_"
        }
        $results += $item
    }

    return $results
}
