param (
    [string]$EnvFile = "$PSScriptRoot\..\.env"
)

if (-not (Test-Path $EnvFile)) {
    Write-Warning "Environment file '$EnvFile' was not found."
    return
}

$loadedCount = 0
Get-Content -Path $EnvFile -Encoding UTF8 | ForEach-Object {
    $line = $_.Trim()
    if ($line -and -not $line.StartsWith("#")) {
        $parts = $line.Split("=", 2)
        if ($parts.Length -eq 2) {
            $key = $parts[0].Trim()
            $val = $parts[1].Trim()
            # Strip outer quotes if present
            if (($val.StartsWith('"') -and $val.EndsWith('"')) -or ($val.StartsWith("'") -and $val.EndsWith("'"))) {
                $val = $val.Substring(1, $val.Length - 2)
            }
            [System.Environment]::SetEnvironmentVariable($key, $val, "Process")
            $loadedCount++
        }
    }
}

$dbHost = if ($env:DB_HOST) { $env:DB_HOST } else { "127.0.0.1" }
$dbPort = if ($env:DB_PORT) { $env:DB_PORT } else { "5432" }
$dbName = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { "docsearch" }
$dbUser = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { "postgres" }
$dbPass = if ($env:POSTGRES_PASSWORD) { $env:POSTGRES_PASSWORD } else { "" }

$connStr = "Host=$dbHost;Port=$dbPort;Database=$dbName;Username=$dbUser;Password=$dbPass"
[System.Environment]::SetEnvironmentVariable("ConnectionStrings__Default", $connStr, "Process")

$apiPort = if ($env:API_PORT) { $env:API_PORT } else { "5000" }
$urls = "http://localhost:$apiPort"
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", $urls, "Process")

Write-Host "Loaded $loadedCount variables from '$EnvFile'."
Write-Host "Set ConnectionStrings__Default for host ${dbHost}:${dbPort} (database: $dbName)."
Write-Host "Set ASPNETCORE_URLS to $urls."
