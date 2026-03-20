[CmdletBinding()]
param()

function Test-Paths {
    param(
        [string]$LogDir = "C:\\Temp\\Logs",
        [int]$MinFreeGB = 10
    )

    $result = [pscustomobject]@{ Name = "Paths/Storage"; Passed = $false; Details = "" }

    try {
        if (-not (Test-Path $LogDir)) {
            New-Item -Path $LogDir -ItemType Directory -Force | Out-Null
        }
        $testFile = Join-Path $LogDir "write-test.txt"
        "ok" | Out-File -FilePath $testFile -Force
        Remove-Item $testFile -Force
    } catch {
        $result.Details = "Cannot write to $LogDir: $_"
        return $result
    }

    try {
        $qualifier = (Split-Path $LogDir -Qualifier).TrimEnd(':')
        $drive = Get-PSDrive -Name $qualifier -ErrorAction Stop
        $freeGB = [math]::Round($drive.Free / 1GB, 2)
        if ($freeGB -lt $MinFreeGB) {
            $result.Details = "Free space ${freeGB}GB < ${MinFreeGB}GB"
            return $result
        }
    } catch {
        $result.Details = "Disk check failed: $_"
        return $result
    }

    $result.Passed = $true
    $result.Details = "$LogDir writable; free space OK"
    return $result
}
