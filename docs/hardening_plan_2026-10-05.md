# Hardening Plan (2026-10-05)

Based on the stability/performance review and the GPT cross-check loop
(see gpt_review_brief / gpt_improvement_proposals / gpt_crosscheck_response).

## Decisions
- Collaboration is an **experimental feature, off by default**. Public release is kept in mind but has lower priority.
- For distribution, the editor should be solid enough for usage testing.
- Implementation order is left to the implementer; each stage ends with tests (existing 326 tests must stay green).

## Stage 1: Save safety (highest priority)
- Atomic project save (write to temp file in same directory, then replace/move).
- AutoSave manifest: do not overwrite a corrupt manifest silently (back it up, then recreate); serialize read-modify-write across processes (named mutex or retry with re-read); ClearSlot under the same protection.
- Instance flag: compare process start time with the timestamp in the flag to defeat PID reuse; if start time is unavailable treat as "unknown" (not alive-proof); dispose Process objects.

## Stage 2: Input validation
- Time signature (numerator/denominator <= 0), invalid BPM, `de_*` parse failures, Freeze/enum values.
- SKB import: FrameAnchor monotonicity check (warn / correct).
- `ChartLayout.ContentHeight` overflow guard.
- Policy: reject + log, never crash.

## Stage 3: Rendering / timing
- Precompute segment start frames in TimingEngine constructor (binary search only for TickToFrame; FrameToTick stays linear or uses its own monotonic search).
- Reduce per-render engine creation; remove O(M^2) measure-line loop.
- Render exceptions: try/finally for Push/Pop balance + logging; no blanket swallowing.

## Stage 4: Audio
- Discard stale `OpenAsync` results; reduce double decode / peak memory.

## Stage 5: Collaboration minimum hardening (experimental)
- ProtocolVersion check, receive size / connection count / timeout limits, tracked client tasks, DisposeAsync awaiting them.
- Validate incoming data; isolate exceptions (verify Dispatcher.Invoke behavior with a small experiment).
- UI label "experimental", default off.

## Deferred (decide when going public)
- Authentication / encryption (self-signed certificate with fingerprint in the invite code).
- Snapshot/diff ordering window (queue or revision based design, requires protocol version bump).
- Loopback-default listening is NOT adopted (conflicts with port-forward usage).

## Status (implemented 2026-10-05/06)
All five stages were implemented. Verified in a Linux build environment: whole solution builds
(WPF app compiled with EnableWindowsTargeting), 775 tests pass. Not verified: WPF runtime behavior
(rendering, audio, dialogs) - needs a manual check on Windows.

- Stage 1: AtomicFile (temp + replace) for project/tab export/autosave slot/manifest; manifest corruption backup
  (manifest.corrupt-*.json); manifest.lock cross-process lock; PID reuse check using process start time.
- Stage 2: TimingValidator (BPM, time signature, FrameAnchor); TimingEngine ctor rejects invalid input;
  ProjectSerializer.Deserialize rejects invalid timing / out-of-range ticks (MaxTick); DOS import falls back to
  default timing with a warning; SKB import skips invalid/non-monotonic timings with warnings;
  ChartLayout.ContentHeight saturates instead of overflowing.
- Stage 3: TimingEngine precomputes segment starts in ctor; TickToFrame/MeasureStartTick/TickToMeasurePosition use
  binary search (equivalence-tested against the previous linear implementation); BpmEvents/TimeSignatures are
  read-only wrappers; ChartCanvas: render exception guard + log (AppLog), try/finally around Push/Pop, measure
  line / time info lane scans start near the viewport, pens hoisted out of loops.
- Stage 4: NAudioBgmPlayer: stale OpenAsync results discarded (generation counter), output setup failure logged,
  lower peak memory when decoding, disposed guard; waveform decode result no longer applied to a different tab.
- Stage 5: CollabHost: hello size/time limit, protocol version check, guest cap (8), name/color sanitizing,
  tracked client tasks awaited on dispose, send timeout, keepalive, generic per-client exception isolation
  (ClientError event); CollabConnection: invalid JSON / "null" body -> InvalidDataException; guest handshake
  timeout; rendezvous helper limits; snapshot is created on the UI thread; remote message apply is
  exception-isolated + tick range validated; menu labeled "experimental" + warning before hosting.

## Known remaining items
- Authentication / encryption for collaboration; snapshot/diff ordering window (see Deferred above).
- Dispatcher.Invoke exception behavior not experimentally verified (mitigated by catching inside the delegate).
- Waveform decoding still reads the audio file separately from the BGM player (double decode).
- Other per-frame allocations in ChartCanvas (per-lane dictionaries, TintCache growth) are not addressed.
- Temporary build archives were left in _to_delete (build_*.tgz, changes.tgz, *_md5.txt); safe to delete.

## Follow-up (2026-10-06): audio path loss and in-window notifications
Cause of "project opens with music not loaded": BASE64 music was decoded into the OS temp folder and that path
was saved in the project; when the OS cleaned it, only the path remained and later drops were stocked as song 2+.
- Decoded music is now stored in ./temp (AppPaths.TempDir) with a content-hash file name (no duplicates).
- Drop routing: song 1 is replaced when its path is empty OR the file does not exist.
- Missing audio on project open: legacy `danoni_music_*` files are searched in ./temp and the OS temp folder and
  copied into ./temp (project path is updated and marked as modified); otherwise a warning is shown.
- Decode failure / audio output failure / render failure / auto-save failure are now reported (previously silent).
- New ToastHost: in-window bottom-right popup, closed with the x button (no auto-dismiss), stacked, de-duplicated.
  Routing: Info = status bar only; Warning/Error = status bar + popup.
