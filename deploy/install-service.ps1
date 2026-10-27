<#
.SYNOPSIS
  Installs or updates PresApp as a Windows service on the server. Run as Administrator.
.DESCRIPTION
  - Copies .\publish to the install folder (default C:\PresApp\app), keeping appsettings.Production.json.
  - Creates the "PresApp" service (automatic start, restart on failure) running as the
    virtual account NT SERVICE\PresApp, and gives that account its SQL login on the database (sqlcmd).
  - On updates, backs up the database first (requires sqlcmd).
.EXAMPLE
  .\deploy\install-service.ps1 -SqlServer "localhost"
#>
param(
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) "publish"),
    [string]$InstallDir = "C:\PresApp\app",
    [string]$BackupDir = "C:\PresApp\backups",
    [string]$SqlServer = "localhost",
    [string]$Database = "PresApp"
)
$ErrorActionPreference = "Stop"
$serviceName = "PresApp"
$exe = Join-Path $InstallDir "PresApp.Api.exe"

if (-not (Test-Path (Join-Path $Source "PresApp.Api.dll"))) { throw "Run deploy\publish.ps1 first ($Source is empty)." }

$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "Backing up $Database before the update..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $BackupDir | Out-Null
    $file = Join-Path $BackupDir ("{0}_preupdate_{1:yyyyMMdd_HHmm}.bak" -f $Database, (Get-Date))
    sqlcmd -S $SqlServer -E -b -Q "BACKUP DATABASE [$Database] TO DISK = N'$file' WITH COPY_ONLY, COMPRESSION, INIT"
    if ($LASTEXITCODE -ne 0) { throw "Backup failed: update cancelled." }
    Stop-Service $serviceName
}

Write-Host "Copying files to $InstallDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force $InstallDir | Out-Null
robocopy $Source $InstallDir /MIR /XF appsettings.Production.json /XD logs /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy code $LASTEXITCODE)." }

$prod = Join-Path $InstallDir "appsettings.Production.json"
if (-not (Test-Path $prod)) {
    @"
{
  "ConnectionStrings": {
    "PresApp": "Server=$SqlServer;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;Application Name=PresApp",
    "Simpro": "Server=$SqlServer;Database=Preservation;Trusted_Connection=True;TrustServerCertificate=True;Application Name=K Data sync"
  },
  "Auth": {
    "CloudflareTeamDomain": "<team>.cloudflareaccess.com",
    "CloudflareAudience": "<AUD tag of the Access application>"
  },
  "Cloudflare": {
    "AccountId": "",
    "ApiToken": "",
    "AccessGroupId": ""
  }
}
"@ | Set-Content -Encoding UTF8 $prod
    Write-Warning "Created $prod - fill in the Cloudflare values before users can sign in."
}

New-Item -ItemType Directory -Force (Join-Path $InstallDir "logs") | Out-Null
icacls (Join-Path $InstallDir "logs") /grant "NT SERVICE\${serviceName}:(OI)(CI)M" | Out-Null

if (-not $service) {
    Write-Host "Creating the service..." -ForegroundColor Cyan
    sc.exe create $serviceName binPath= "`"$exe`"" start= delayed-auto obj= "NT SERVICE\$serviceName" DisplayName= "PresApp" | Out-Null
    sc.exe description $serviceName "PresApp - preservation tracking (API + web)" | Out-Null
    sc.exe failure $serviceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")
}

# The service account NT SERVICE\PresApp exists now: give it its SQL login and db_owner on the PresApp database
# (safe to repeat on every update).
Write-Host "Granting the service access to $Database..." -ForegroundColor Cyan
$grant = @"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT SERVICE\$serviceName')
    CREATE LOGIN [NT SERVICE\$serviceName] FROM WINDOWS WITH DEFAULT_DATABASE = [$Database];
USE [$Database];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'NT SERVICE\$serviceName')
    CREATE USER [NT SERVICE\$serviceName] FOR LOGIN [NT SERVICE\$serviceName];
ALTER ROLE [db_owner] ADD MEMBER [NT SERVICE\$serviceName];
"@
sqlcmd -S $SqlServer -E -b -Q $grant
if ($LASTEXITCODE -ne 0) { throw "Could not grant the service access to $Database. Run deploy\sql\01-create-database.sql first and check that you are a SQL Server sysadmin." }

Start-Service $serviceName
Start-Sleep -Seconds 5
try {
    $health = Invoke-RestMethod "http://127.0.0.1:5080/api/health" -TimeoutSec 15
    Write-Host "PresApp is running: $($health.status), version $($health.version)" -ForegroundColor Green
} catch {
    Write-Warning "The service started but /api/health did not answer. Check $InstallDir\logs."
}
