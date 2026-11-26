# Hopkey Automated Test Doc

The executor runs the steps in order and records the actual output of each one. Do not change any code or test files. Run every command from PowerShell in `E:\workspace\www\Hopkey`.

## T1 Build
```powershell
dotnet build Hopkey.sln -c Release -nologo
```
Expected: `Build succeeded` with 0 errors. Record the warning count. If it fails, quote the first 20 error lines verbatim.

## T2 Core unit and integration tests
```powershell
dotnet run --project tests/Hopkey.SelfTest -c Release --no-build -- --no-perf
```
Expected: the last line is `RESULT: N passed, 0 failed ...` and the exit code is 0.
Record the RESULT line and every `FAIL` line verbatim, including any exception stack.

## T3 Search performance benchmark (2 million synthetic entries)
```powershell
dotnet run --project tests/Hopkey.SelfTest -c Release --no-build -- --perf-only
```
Record verbatim: the `machine:` line, the `synthetic build:` line, every `query ...` line, every `PERF ...` line, the `index.bin ...` line, the `prune ...` line, and the final RESULT and PERF-WARN lines.

## T4 Real-directory crawl benchmark
```powershell
dotnet run --project tests/Hopkey.SelfTest -c Release --no-build -- --perf-only --entries 100000 --crawl C:\Windows
```
Record the `crawled ...` line and any `USN/MFT build` line.

## T5 Application smoke test
The exe is at `src\Hopkey.App\bin\Release\net10.0-windows\win-x64\Hopkey.exe`. If it is not there, locate `Hopkey.exe` under `src\Hopkey.App\bin\Release` and record the actual path.

1. Clean the environment: `Get-Process Hopkey -ErrorAction SilentlyContinue | Stop-Process -Force`, then wait 1 second.
2. Note the start time, run `Start-Process <exe path>`, then wait 10 seconds.
3. Confirm the process is still alive: `Get-Process Hopkey`. Record the PID and `WorkingSet64` in MB.
4. Check that `$env:APPDATA\Hopkey\settings.json` exists and parses: `Get-Content ... -Raw | ConvertFrom-Json`. Record `general.language` and `hotkeys.launcher`.
5. Read today's log at `$env:LOCALAPPDATA\Hopkey\logs\yyyy-MM-dd.log`, newest content only. Record:
   - whether these lines are present: `WinEvent hooks installed`, `Keyboard hook installed`, `App catalog:`
   - every line containing `[ERROR]` or `[WARN]`, verbatim
6. Wait for indexing to finish. Poll the log every 15 seconds, for at most 10 minutes, until a line containing `Index saved` appears. Record verbatim every line containing `Crawl `, `USN build`, `Index cache loaded`, `Index saved` and `Pruned`, and note the total wait time.
7. Record the process `WorkingSet64` in MB again, after indexing.
8. Single-instance check: run `Start-Process <exe path>` again and wait 5 seconds. `Get-Process Hopkey` must still show exactly 1 process with the same PID as before. Confirm that the log gained the line `Another instance is running`.
9. Cache load check:
   - Run `Stop-Process -Name Hopkey -Force` and wait 2 seconds.
   - Start the exe again and wait 15 seconds.
   - Record the new `Index cache loaded` line, including its time in ms, and any `USN catch-up` or `Crawl ` lines.
10. Cleanup: run `Get-Process Hopkey | Stop-Process -Force`.
11. Look for crash logs: `Get-ChildItem $env:LOCALAPPDATA\Hopkey\logs\crash-*.log`. If any exist, record the first 30 lines of each.

## Report format
- One row per step, T1 through T5 (with T5's sub-steps), marked PASS, FAIL or INFO, with the key output beside it.
- Quote all FAIL, ERROR, WARN and PERF lines verbatim.
- Report facts only. Do not analyze causes or suggest fixes.
