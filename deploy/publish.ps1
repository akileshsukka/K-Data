<#
.SYNOPSIS
  Builds PresApp (React front + .NET API) into .\publish, ready to copy to the server.
.EXAMPLE
  .\deploy\publish.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "publish"

Write-Host "1/3 Building the front end..." -ForegroundColor Cyan
Push-Location (Join-Path $root "web")
npm ci
npm run build
Pop-Location

Write-Host "2/3 Running the tests..." -ForegroundColor Cyan
dotnet test (Join-Path $root "PresApp.sln") -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed: nothing was published." }

Write-Host "3/3 Publishing the API..." -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root "src\PresApp.Api\PresApp.Api.csproj") -c Release -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host "Ready: $out" -ForegroundColor Green
