#!/usr/bin/env bash
# Runs everything that backs the post's claims. Exits non-zero if anything fails.
set -euo pipefail
cd "$(dirname "$0")"

echo "dotnet10-sse-angular-demo - checking the post's claims"
echo "  1. Integration tests: the raw text/event-stream through WebApplicationFactory"
echo "  2. Angular build into Api/wwwroot"
echo "  3. Playwright in Chromium: reconnect + Last-Event-ID, the 401, cookies, tab close"
echo

dotnet test SseDemo.slnx --logger "console;verbosity=normal"
(cd client && npm ci --no-audit --no-fund && npx ng build && npx playwright test)
