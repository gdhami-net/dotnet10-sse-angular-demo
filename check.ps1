# Runs everything that backs the post's claims. Exits non-zero if anything fails.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host 'dotnet10-sse-angular-demo - checking the post''s claims'
Write-Host '  1. Integration tests: the raw text/event-stream through WebApplicationFactory'
Write-Host '  2. Angular build into Api/wwwroot'
Write-Host '  3. Playwright in Chromium: reconnect + Last-Event-ID, the 401, cookies, tab close'
Write-Host ''

dotnet test SseDemo.slnx --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Push-Location client
try {
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    npx ng build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    npx playwright test
    exit $LASTEXITCODE
} finally { Pop-Location }
