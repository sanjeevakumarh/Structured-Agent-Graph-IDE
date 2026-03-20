[CmdletBinding()]
param()

<#
.SYNOPSIS
    Smoke tests for a running SAGIDE.Service instance.

.DESCRIPTION
    Exports a single function  Test-SmokeTests  that targets a live REST API
    and returns an array of  [pscustomobject]@{Name; Passed; Details}  objects,
    one per test case.  Follows the same pattern as the other Test-*.ps1 scripts
    so the master orchestrator (Invoke-EndToEndTest.ps1) can include results in
    the shared pass/fail table.

    Tests cover:
      - GET  /api/health           → 200 + "ok"
      - GET  /api/tasks            → 200 JSON array
      - POST /api/tasks (valid)    → 201 + taskId
      - GET  /api/tasks?tag=...    → 200, submitted task visible
      - GET  /api/tasks/{id}       → 200 or 404 (may have already processed)
      - POST /api/tasks (no desc)  → 400
      - GET  /api/tasks/{unknown}  → 404
      - DELETE /api/tasks/{id}     → 200
      - GET  /api/prompts          → 200 JSON array
      - GET  /api/prompts/{bogus}  → 200 empty array
      - GET  /api/results          → 200 JSON array
      - GET  /api/results?tag=...  → 200 JSON array
      - GET  /                     → 200 HTML (dashboard)
      - GET  /api/reports          → 200
#>

function Test-SmokeTests {
    param(
        [string]$BaseUrl   = "http://localhost:5100",
        [int]$TimeoutSec   = 8
    )

    $results  = [System.Collections.Generic.List[pscustomobject]]::new()
    $smokeTag = "e2e-smoke-$([datetime]::UtcNow.ToString('yyyyMMddHHmmss'))"
    $submittedTaskId = $null

    # ── helpers ──────────────────────────────────────────────────────────────

    # Wraps Invoke-WebRequest so that non-2xx responses are captured rather
    # than thrown.  Works on both PowerShell 5.1 and PowerShell 7+.
    function Invoke-Api {
        param(
            [string]$Method,
            [string]$Path,
            [hashtable]$Body    = $null
        )
        $url = "$BaseUrl$Path"
        $r   = [pscustomobject]@{ Status = $null; Body = ''; Error = '' }

        $params = @{
            Method          = $Method
            Uri             = $url
            UseBasicParsing = $true
            TimeoutSec      = $TimeoutSec
        }
        if ($Body) {
            $params.Body        = ($Body | ConvertTo-Json -Compress -Depth 5)
            $params.ContentType = 'application/json'
        }

        try {
            # PS 7+ can skip throwing on non-2xx
            if ($PSVersionTable.PSVersion.Major -ge 7) {
                $params.SkipHttpErrorCheck = $true
            }
            $resp     = Invoke-WebRequest @params
            $r.Status = [int]$resp.StatusCode
            $r.Body   = $resp.Content
        } catch {
            # PS 5.1 path: non-2xx throws WebException
            if ($_.Exception.Response) {
                $r.Status = [int]$_.Exception.Response.StatusCode
                try {
                    $stream = $_.Exception.Response.GetResponseStream()
                    $r.Body = [System.IO.StreamReader]::new($stream).ReadToEnd()
                } catch { }
            }
            $r.Error = $_.Exception.Message
        }
        return $r
    }

    function Pass([string]$Name, [string]$Details) {
        [pscustomobject]@{ Name = $Name; Passed = $true; Details = $Details }
    }
    function Fail([string]$Name, [string]$Details) {
        [pscustomobject]@{ Name = $Name; Passed = $false; Details = $Details }
    }

    # ── 1. Health ─────────────────────────────────────────────────────────────
    $r = Invoke-Api GET /api/health
    if ($r.Status -eq 200 -and $r.Body -match '"ok"') {
        $results.Add((Pass "GET /api/health" "200 OK — service healthy"))
    } else {
        $results.Add((Fail "GET /api/health" "Expected 200/'ok'; got $($r.Status) $($r.Error)"))
    }

    # ── 2. GET /api/tasks → JSON array ───────────────────────────────────────
    $r = Invoke-Api GET /api/tasks
    if ($r.Status -eq 200 -and $r.Body.Trim().StartsWith('[')) {
        $results.Add((Pass "GET /api/tasks" "200 OK — JSON array"))
    } else {
        $results.Add((Fail "GET /api/tasks" "Expected 200 []; got $($r.Status)"))
    }

    # ── 3. POST /api/tasks (valid) → 201 + taskId ────────────────────────────
    $r = Invoke-Api POST /api/tasks @{ description = "E2E smoke test"; sourceTag = $smokeTag }
    if ($r.Status -eq 201 -and $r.Body -match '"taskId"') {
        $m = [regex]::Match($r.Body, '"taskId"\s*:\s*"([^"]+)"')
        $submittedTaskId = if ($m.Success) { $m.Groups[1].Value } else { $null }
        $results.Add((Pass "POST /api/tasks (valid)" "201 Created — taskId=$submittedTaskId"))
    } else {
        $results.Add((Fail "POST /api/tasks (valid)" "Expected 201+taskId; got $($r.Status): $($r.Error)"))
    }

    # ── 4. GET /api/tasks?tag → task visible in DB ───────────────────────────
    $r = Invoke-Api GET "/api/tasks?tag=$smokeTag"
    if ($r.Status -eq 200 -and $r.Body.Trim().StartsWith('[')) {
        $hasTask = $r.Body -match $smokeTag
        if ($hasTask) {
            $results.Add((Pass "GET /api/tasks?tag" "200 OK — submitted task found by source tag"))
        } else {
            $results.Add((Fail "GET /api/tasks?tag" "200 but smoke tag not in response body"))
        }
    } else {
        $results.Add((Fail "GET /api/tasks?tag" "Expected 200 []; got $($r.Status)"))
    }

    # ── 5. GET /api/tasks/{id} — in-memory (may already be processed) ────────
    if ($submittedTaskId) {
        $r = Invoke-Api GET "/api/tasks/$submittedTaskId"
        if ($r.Status -eq 200) {
            $results.Add((Pass "GET /api/tasks/{id}" "200 OK — task still in orchestrator memory"))
        } elseif ($r.Status -eq 404) {
            # Task may have been processed and evicted from in-memory queue —
            # not a failure as long as it was visible via DB (test 4).
            $results.Add((Pass "GET /api/tasks/{id}" "404 — task already processed/evicted from queue (DB confirm via tag query)"))
        } else {
            $results.Add((Fail "GET /api/tasks/{id}" "Expected 200 or 404; got $($r.Status)"))
        }
    } else {
        $results.Add((Fail "GET /api/tasks/{id}" "Skipped — no taskId from POST step"))
    }

    # ── 6. POST /api/tasks without description → 400 ─────────────────────────
    $r = Invoke-Api POST /api/tasks @{ agentType = "Generic" }
    if ($r.Status -eq 400) {
        $results.Add((Pass "POST /api/tasks (missing desc)" "400 Bad Request as expected"))
    } else {
        $results.Add((Fail "POST /api/tasks (missing desc)" "Expected 400; got $($r.Status)"))
    }

    # ── 7. GET /api/tasks/{unknown} → 404 ────────────────────────────────────
    $fakeId = "smoke-no-such-$(New-Guid)"
    $r = Invoke-Api GET "/api/tasks/$fakeId"
    if ($r.Status -eq 404) {
        $results.Add((Pass "GET /api/tasks/{unknown}" "404 Not Found as expected"))
    } else {
        $results.Add((Fail "GET /api/tasks/{unknown}" "Expected 404; got $($r.Status)"))
    }

    # ── 8. DELETE /api/tasks/{id} → 200 ──────────────────────────────────────
    $cancelId = if ($submittedTaskId) { $submittedTaskId } else { "smoke-phantom-$(New-Guid)" }
    $r = Invoke-Api DELETE "/api/tasks/$cancelId"
    if ($r.Status -eq 200) {
        $results.Add((Pass "DELETE /api/tasks/{id}" "200 OK — cancel idempotent"))
    } else {
        $results.Add((Fail "DELETE /api/tasks/{id}" "Expected 200; got $($r.Status)"))
    }

    # ── 9. GET /api/prompts → JSON array ─────────────────────────────────────
    $r = Invoke-Api GET /api/prompts
    if ($r.Status -eq 200 -and $r.Body.Trim().StartsWith('[')) {
        $count = ([regex]::Matches($r.Body, '"name"\s*:')).Count
        $results.Add((Pass "GET /api/prompts" "200 OK — $count prompt(s) loaded"))
    } else {
        $results.Add((Fail "GET /api/prompts" "Expected 200 []; got $($r.Status)"))
    }

    # ── 10. GET /api/prompts/{bogus} → 200 empty array ───────────────────────
    $r = Invoke-Api GET "/api/prompts/nonexistent-smoke-domain"
    if ($r.Status -eq 200 -and $r.Body.Trim() -eq '[]') {
        $results.Add((Pass "GET /api/prompts/{bogus domain}" "200 OK — empty array for unknown domain"))
    } else {
        $results.Add((Fail "GET /api/prompts/{bogus domain}" "Expected 200 []; got $($r.Status) '$($r.Body.Trim())'"))
    }

    # ── 11. GET /api/results → JSON array ────────────────────────────────────
    $r = Invoke-Api GET /api/results
    if ($r.Status -eq 200 -and $r.Body.Trim().StartsWith('[')) {
        $results.Add((Pass "GET /api/results" "200 OK — JSON array"))
    } else {
        $results.Add((Fail "GET /api/results" "Expected 200 []; got $($r.Status)"))
    }

    # ── 12. GET /api/results?tag={smokeTag} → 200 ────────────────────────────
    $r = Invoke-Api GET "/api/results?tag=$smokeTag"
    if ($r.Status -eq 200) {
        $results.Add((Pass "GET /api/results?tag" "200 OK — result filter by source tag"))
    } else {
        $results.Add((Fail "GET /api/results?tag" "Expected 200; got $($r.Status)"))
    }

    # ── 13. GET / → 200 HTML dashboard ───────────────────────────────────────
    $r = Invoke-Api GET /
    if ($r.Status -eq 200 -and $r.Body -match '(?i)<html') {
        $results.Add((Pass "GET / (dashboard)" "200 OK — HTML dashboard served"))
    } else {
        $results.Add((Fail "GET / (dashboard)" "Expected 200 HTML; got $($r.Status)"))
    }

    # ── 14. GET /api/reports → 200 ───────────────────────────────────────────
    $r = Invoke-Api GET /api/reports
    if ($r.Status -eq 200) {
        $results.Add((Pass "GET /api/reports" "200 OK — reports listing"))
    } else {
        $results.Add((Fail "GET /api/reports" "Expected 200; got $($r.Status)"))
    }

    return $results.ToArray()
}
