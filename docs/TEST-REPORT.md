# Hopkey Test Report (Round 1)

Date: 2026-11-25. Environment: Windows 11 Pro 26300, 16 logical cores (AVX2+AVX512), .NET SDK 10.0.401, not elevated (crawler indexing; the MFT/USN path was not covered).

## Automated tests (`tests/Hopkey.SelfTest`, see TEST-AUTOMATED.md)
- Functional: **127/127 passed**. Covers HotkeySpec, Calculator, QueryParser, ExclusionMatcher (name/path/wildcard/regex/attributes/Diff/fingerprint/validation/import parsing), NameMatcher, pinyin, TopN, the end-to-end crawl → search → live updates (create, rename, delete, folder moved in, folder rename) → save/load → compaction flow, exclusion pruning, and history.
- Performance on a 2M-entry synthetic index:

| Item | Before | After fixes | Target |
|---|---|---|---|
| Search p95 (worst multi-char query) | 24.2 ms | **6.8 ms** | ≤ 10 ms |
| Search p95 single char "a" | 40 ms | **7.9 ms** | ≤ 25 ms |
| Cache load until searchable | 111 ms | **97 ms** | ≤ 1000 ms |
| Add exclusion rule (prune) | 203 ms | **89 ms** | ≤ 1500 ms |
| Memory per entry (synthetic) | 111 B | 111 B | ≤ 140 B |

- Real disks (1.29M entries, non-admin crawl): first build of C: 6.4 s (748k entries), D: 1.1 s, E: 1.8 s. Cache load 135 ms.
- Process memory (1.29M entries): working set **556 MB → 330 MB**. About 180 MB of that is the WPF/D3D/runtime baseline (measured with an empty index); the index itself is about 152 MB managed.

## GUI tests (Notepad Open/Save dialogs + File Explorer)
| Item | Result |
|---|---|
| Open dialog detected, toolbar shown under the dialog | PASS (22 ms to show the toolbar once the dialog is ready) |
| Dialog follows when you switch to Explorer and back | PASS (65 ms navigation) |
| Ctrl+G jumps to the recommended folder | PASS |
| Save dialog follows and keeps the typed filename `my-report.txt` | PASS (73 ms); no file saved by mistake |
| Toolbar hides and releases its owner when the dialog closes | PASS |

## Defects found and fixed this round
1. Renaming or moving an item under an excluded location deleted it by mistake. Rename used the full-path exclusion check while create used the per-level check; both now use the per-level check.
2. Search was slow (worst query p95 24 ms):
   - hits now map to entries with galloping search;
   - the pinyin scan runs in parallel;
   - phase 2 uses an adaptive K, does no per-candidate allocation, and computes highlights only for the final results;
   - the multi-keyword memo uses integer keys;
   - incremental narrowing now also works with pinyin enabled.
3. Memory:
   - completed startup tasks kept the replaced cached volumes alive;
   - DirPaths is now keyed by a 64-bit hash (37 MB → 3 MB);
   - GC runs in Aggressive mode after indexing, with ConserveMemory enabled.
4. settings.json / history.json saved with a UTF-8 BOM (Notepad or PowerShell) were treated as corrupt and reset to defaults.
5. QuickSwitch did not appear on the first open: the dialog takes the foreground before its child controls exist. Detection now retries (15 ms to 480 ms backoff).
6. The toolbar stole the foreground and WPF moved it to the wrong position. Fixes:
   - give the foreground back after Show;
   - WM_ACTIVATE returns activation to the dialog;
   - WM_WINDOWPOSCHANGING forces our computed position.
7. Modern Save dialogs were misclassified as folder pickers: the "Save as type" combo has id 0. The new rule is "ComboBox without an Edit child".
8. The IME pinyin enrichment retried on every start, and a failed secondary hotkey popped a balloon on every start. Both are now quiet.

## Not covered (needs manual verification)
- **Launcher window and the toolbar's own menus/buttons:** the user did not authorize automated control of Hopkey's windows, so these were not clicked or typed into. The search core is covered by the automated tests.
- **Type-to-search in Explorer:** File Explorer only allows clicks in the test tool, so typing could not be tested.
- **MFT/USN indexing (admin) and UIPI with elevated programs:** this round ran without elevation.
- **Settings window, theme/language switching, multiple monitors and DPI switching.**

Debug logging: set `HOPKEY_DEBUG=1` before starting to log foreground and detection details at DEBUG level.

---

# Round 2: Hands-on GUI tests (launcher / toolbar menus / settings window)

The user authorized control of Hopkey's windows for this round.

## Results
| Area | Item | Result |
|---|---|---|
| Launcher | Opens on double-Ctrl and keeps the foreground | PASS (shown in 12–73 ms) |
| Launcher | Prefix/substring search, highlighting, middle-shortened paths, Alt+N | PASS |
| Launcher | Multiple keywords with a path keyword (`hopkey plan` → PLAN.md) | PASS |
| Launcher | Chinese search (a Chinese folder name), pinyin initials of that name | PASS |
| Launcher | Calculator (`=12*3+4` → 40), web search (`bd …`), no-results web fallback | PASS |
| Launcher | Tab action menu (8 actions), Ctrl+Enter opens the folder with the item selected | PASS |
| Launcher | Empty query shows recently used, favorites and recent folders; adding a favorite | PASS |
| Launcher | Excluded locations don't appear (`%TEMP%`=E:\tmp is excluded by default) | PASS |
| Toolbar | Open-folders menu (z-order) / favorites / recent (current folder checked) → navigates the dialog | PASS |
| Toolbar | More menu → QuickSwitch settings | PASS |
| Toolbar | Search button → context-mode launcher → Enter navigates the dialog | PASS |
| Settings | Live language switch zh ⇄ en, live theme switch (window and toolbar) | PASS (after fixes) |
| Settings | Add a name exclusion rule → index pruned immediately (26,989 entries / 309 ms) → no longer searchable | PASS |
| Settings | Remove the rule → re-crawl restores the entries | PASS |
| Settings | Remove a favorite | PASS |

## Defects found and fixed this round
1. **OneDrive (cloud files) not indexed at all:** OneDrive folders are reparse points, and the crawler skipped every reparse point. It now reads the reparse tag (FindFirstFileEx) and descends only into `IO_REPARSE_TAG_CLOUD*` folders not marked `RECALL_ON_OPEN`. Symlinks and junctions are still skipped. C: crawl time is unchanged (2.5 s); E: gained about 8k entries.
2. **The first launcher show after startup lost the foreground immediately** (activated, then deactivated 17 ms later). Losing activation within 300 ms of showing now triggers re-activation (at most twice). Pre-warm no longer shows and hides the window.
3. **No toolbar when the dialog was already in the foreground at startup:** no foreground event fires in that case. Startup and resume now check the current foreground window once.
4. Opening the launcher via a second instance: the second instance now calls `AllowSetForegroundWindow` for the running instance.
5. **Settings: the Theme drop-down went blank after switching language** (ItemsSource replaced): the selected value is re-pushed, and Language now ignores null.
6. **Settings: DataGrid column headers didn't follow the language** (DataGridColumn is not in the visual tree): headers are now set in code.
7. **Launcher status bar:** the text didn't follow the language, and the count didn't update after a prune or live update. It now refreshes on language change and on `FileIndex.Changed`.

## Testing notes
- Double-tap Ctrl: the test tool's key-repeat interval is about 770 ms, longer than the 300 ms double-tap window, so the hotkey was triggered with real SendInput (120 ms apart). The test-only variable `HOPKEY_DOUBLETAP_MS` can widen the window; the default is still 300 ms.
- The test tool restores focus to the target app after its key operations and hides/restores unauthorized windows before screenshots. This disturbs foreground measurements and is not product behavior.
- The test tool's mouse clicks land about 12 px left of the screenshot coordinates (multi-monitor/DPI). The search button was confirmed by both UI Automation Invoke and a corrected click.

## Still not covered
- Type-to-search inside File Explorer: the test tool forbids typing into Explorer.
- MFT/USN and dialogs of elevated programs (requires running as administrator).
- Multi-monitor and DPI switching.


---

# 第三轮：管理员模式测试 (Round 3: elevated)

Date: 2026-11-26. Hopkey ran as administrator (UAC approved by the user), with an elevated helper PowerShell running the test scripts (UI Automation, SendInput) for the elevated windows. The test dialogs were WinForms Open/Save dialogs hosted in an elevated PowerShell.

## Results
| Area | Item | Result |
|---|---|---|
| MFT/USN | First build, all 3 volumes in parallel (C: 902,747 / D: 226,681 / E: 323,968) | PASS, ~2 s from start to all indexed (C: 1.45–1.6 s, D: 0.9 s, E: 0.75 s) |
| MFT/USN | Single-volume benchmark (`--crawl`, elevated) | PASS: C: 903k in 1.13–1.36 s, E: 324k in 0.33 s (target 1M < 3 s) |
| MFT/USN | Entries vs. non-admin crawler (diff in the self-test benchmark) | PASS after fix 1: D:/E: identical. C: USN has +44.9k admin-only paths; 328 AppData paths "missing" come from MSIX filesystem virtualization of the test environment (the crawler ran inside the Claude app's package context) |
| MFT/USN | Cache load + USN catch-up after restart | PASS: 1.45M entries loaded in 71–79 ms, catch-up 12 ms; 5 items changed while the app was off (create, rename, hard link, new folder + file) all found |
| MFT/USN | Live updates under `tests\` (create/rename/hard link/delete) | PASS: applied 0.45–0.7 s after the change; verified in the launcher (2 links → 1 → 0) |
| MFT/USN | Memory after startup | managed 188–193 MB, working set 329–351 MB (1.45M entries) |
| Exclusions (USN) | Add a name rule in settings → prune | PASS: 6 entries pruned in 322 ms (+1 s debounce); a new file in the excluded folder is not indexed |
| Exclusions (USN) | Remove the rule → rebuild | PASS: all 3 volumes rebuilt in 1.45 s, all entries back; memory back to 197 MB managed after fix 5 |
| QuickSwitch, CL not elevated | Toolbar on an elevated dialog | PASS after fix 2: shown topmost with ⚠ button. Navigation is rejected immediately (red flash + "目标程序以管理员身份运行…"), no hang |
| QuickSwitch, CL not elevated | Ctrl+G / double-Ctrl while an elevated window has focus | Not triggered: Windows doesn't deliver input aimed at elevated windows to a non-elevated low-level hook (same for Listary). Documented limitation |
| QuickSwitch, CL elevated | Ctrl+G, Explorer-folders menu, follow Explorer, Save keeps file name | PASS (C:\Windows → E:\workspace\www → OneDrive\Utilities → D:\Downloads; `elevated-report.txt` kept) |
| Restart as admin | ⚠ button → UAC → old instance exits, `--wait-pid` new one starts, re-attaches to the dialog, opens settings | PASS |
| Single instance | Second start elevated→elevated and non-elevated→elevated | PASS: running launcher opens (1.7 ms); no crash |
| Tray | `TaskbarCreated` reaches the elevated process (icon restored after an Explorer restart) | PASS (posted from a non-elevated process) |
| Autostart | Elevated task create/remove via settings | PASS after fixes 6/7; system left with no task and no Run value |

## Defects found and fixed this round
1. **USN index lost every extra hard-link name.** `FSCTL_ENUM_USN_DATA` returns one name per file. Effect: 107k paths on C: (System32 language files and Start Menu shortcuts hard-linked into the excluded WinSxS, Edge, WindowsApps) and 25.8k on E: (Rust/pnpm build output). New enumeration, fastest first:
   - `FSCTL_GET_NTFS_FILE_RECORD` per record: parallel, walking downwards, which skips free records; it is faster than the old path.
   - `FSCTL_QUERY_FILE_LAYOUT`, which is refused on the system volume.
   - Raw `$MFT` read with data runs and fixups (`MftReader`), which is refused where raw volume reads are blocked (this machine's C:).
   - `FSCTL_ENUM_USN_DATA` as the last resort.

   Extra links get synthetic keys (`LinkKey`: FRN | bit 47 | ordinal << 40). The journal applier handles `HARD_LINK_CHANGE`, renames of one link, deletion of all links, and attributes shared by all links. The cache format went to v3 so old caches are rebuilt once. 14 new self-test checks.
2. **Non-elevated CL: toolbar sank behind other windows on elevated dialogs.** UIPI makes setting the owner fail silently. CL now detects the failed owner and falls back to topmost while the dialog is active, hiding the toolbar when another app takes the foreground.
3. **Log lines lost when two instances write the same log** (restart as admin, second instance). The logger now seeks to the end before every write.
4. **Fresh builds not saved until exit or the 10-minute timer.** Bulk-built volumes weren't marked dirty, so a crash or kill forced a full rebuild on the next start.
5. **Memory after a rebuild doubled** (managed 382 MB, working set 650 MB). The search narrowing cache kept the replaced volumes alive during the trim. It is now dropped on `FileIndex.VolumesReplaced`, and the trim runs once after the last concurrent build.
6. **Unticking "开机自动启动" left the elevated logon task in place** while "以管理员身份" stayed ticked, so the app still started at every logon. The task is now removed whenever autostart is off. A non-elevated instance asks for UAC to delete it, and only when the user changes the setting. An old leftover task is removed on the next elevated start. Autostart apply now runs off the UI thread.
7. **Logon task created with `schtasks /SC ONLOGON` defaults:**
   - "start only on AC power" and "stop when going on battery": on this laptop, unplugging killed the app;
   - a 72 h time limit;
   - below-normal priority 7.

   The task is now registered from XML: battery allowed, no time limit, priority 4, trigger limited to this user.
8. Ctrl+G or a menu jump to the folder the dialog already shows waited 2 s, then flashed "Timeout". It now returns immediately. (Not specific to admin mode.)

Self-test: `RESULT: 141 passed, 0 failed` (127 + 14 hard-link checks), perf targets unchanged (2M p95 6.8 ms).

## Notes
- The elevated instance started by "restart as admin" doesn't inherit `HOPKEY_DEBUG` (runas starts with a fresh environment).
- The self-test crawl benchmark (`--crawl X:\`, elevated) now times every USN method and prints a crawler-vs-USN path diff.
- The `Ctrl+Alt+Space` RegisterHotKey failure in the logs is a conflict with another program (Listary is installed), not a defect.

## Not covered
- Follow-up: self-test re-run after all fixes: 141/141. Fix 8 re-checked live in Notepad's Open dialog: two Ctrl+G jumps to the folder already shown took 2.1 ms and 0.5 ms, no timeout. Fix 7 (the XML task): the user tested a real logon on 2026-11-26 and it passed.
- Real Explorer restart with the elevated tray icon (simulated only), multi-user logon, ReFS/FAT volumes (they use the crawler), volumes where every full-fidelity method fails (only `ENUM_USN_DATA` left, so hard links are missing again).
- Elevated Win11 Notepad (packaged) dialogs. The test used WinForms common dialogs.
