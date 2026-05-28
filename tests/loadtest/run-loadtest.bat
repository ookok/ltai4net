REM LTAI v7.0 Load Test Runner
REM Prerequisites: k6 (https://k6.io/docs/get-started/installation/)
REM Start LTAI.Host first, then run this script.

echo === LTAI v7.0 Load Test ===
echo.

REM Smoke test (5 VUs, 30s)
echo [1/3] Smoke test...
k6 run tests/loadtest.js --vus 5 --duration 30s --summary-export=loadtest-smoke.json

REM Load test (100 VUs, staged)
echo [2/3] Full load test...
set LTAI_BASE_URL=http://localhost:8080
k6 run test/loadtest.js --out json=loadtest-results.json

echo [3/3] Summary: loadtest-summary.json
echo.
echo Done.
