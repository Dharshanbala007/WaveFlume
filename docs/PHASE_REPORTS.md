# Phase reports

## Phase 1 — Physics + tests (no UI) — 2026-10-30

### 1. Tree
See `docs/README.md` → Layout.

### 2. Source
- **Core:** `Enums`, `HardLimits` (0.45 m / 1.57 m/s / 0.30 s compile-time ceilings), `Units` (incl. `MmToMotorCounts`), `FlumeConfig` (+ `DriveConfig`, `OperationalLimits`, `RangeBounds`, `Validate`, `Load`), `WaveRequest`, `Trajectory`, `SafetyReport`.
- **Physics:** `Dispersion` (Newton–Raphson, §7.1), `TransferFunctions` (piston, elevated hinge, flap, §7.2), `WaveComponent` (X_a = H/2c0), `OrientationPhase` (§7.8), `Ramp` (§7.9), `TrajectoryGenerator` (monochromatic/bichromatic/oblique, 6 × N float32), `SafetyGatekeeper` (§8, full-trajectory scan), `LimitCurve` (§7.11), `WaveStatistics` (zero-upcrossing + Welch/Hann, §7.12), `WaveMathEngine` (§6, §7.10 derived outputs).

### 3. Packages
- `WaveFlume.Physics`: MathNet.Numerics 5.0.0
- Tests: xUnit 2.5.3, FluentAssertions 6.12.0

### 4. Build / test
`dotnet build WaveFlume.sln` → 0 warnings, 0 errors. `dotnet test WaveFlume.sln` → 65 passed.

### 5. Acceptance checklist
- [x] Golden case (h 0.6, H 0.05, T 1, piston) within 1 %: k, L, C, c0, X_a, u, V_peak, A_peak, power, force
- [x] Newton solver converges on T ∈ [0.3, 6] × h ∈ [0.2, 1.0]
- [x] Flap == elevated hinge at h0 = 0 == closed form
- [x] Piston c0 > 1.99 at kh = 6, c0 ≈ kh as kh → 0
- [x] Gatekeeper rejects T = 0.29 s, X = 0.46 m, V = 1.6 m/s, missing a_max, over-acceleration, X(0) ≠ 0, low fs, breaking, phase step ≥ π (warns ≥ π/2)
- [x] Config cannot raise the hard ceilings; repo `flume_config.json` loads and validates
- [x] Per-paddle phase formula, ramp endpoints (X and V zero at start/end)
- [x] Limit curve: breaking on the left, kinematic limit on the right, inside/outside test
- [x] Zero-upcrossing stats on a regular sine (exact) and a narrow-band Rayleigh sea; Welch m0 = variance
- [x] Motor-count round trip
- [ ] Random/focusing/solitary/user-defined, 2nd order — Phase 4 (engine throws `NotImplementedException` with a clear message)

### 6. Decisions and open questions
- **Force estimate uses W = 3 m, not b = 0.5 m.** §7.10 says width b, but the §14 golden value (177 N) only matches the full flume width (b gives 29.6 N). Implemented to match the golden case; listed in `TODO_VERIFY.md` #1. Please confirm.
- **Bichromatic panel values** (stroke, velocity, acceleration, force) are the sum of component amplitudes — an upper bound. The gatekeeper always uses the exact trajectory.
- **Signal duration** = `NumWaves × PeriodS` (primary period for bichromatic); the ramp-up and ramp-down fit inside it, and a ramp longer than half the duration is rejected.
- **Ursell / kh < 0.3 warnings** are raised for first-order waves too, not only second order.
- Open inputs still needed (§16): max acceleration, encoder resolution, confirmed max water depth, PLC vendor + memory map, block-telemetry feasibility, probe count/positions/ranges, piston vs flap + hinge height, absorber/beach, Darbyshire and Schäffer F±/G± formulas, logo/report template, HBM protocol.

## Phase 2 — Simulator + PLC layer — 2026-10-30

### 1. Tree
See `docs/README.md` → Layout. New: `src/WaveFlume.Plc`, `src/WaveFlume.Data`, `tests/WaveFlume.Plc.Tests`, `tests/WaveFlume.Data.Tests`, `config/plc_profiles/`, `docs/PLC_IO_MAP.md`.

### 2. Source
- **Plc:** `IPlcDriver` (spec §9.1 signature), `TagMap` (logical signal → address/type/scale/offset/access, JSON profiles, validation), `WordCodec` (ABCD/CDAB/BADC/DCBA), `TrajectoryChunker` + `Crc16`, `TagMapDriverBase` (all IPlcDriver behaviour expressed through the TagMap, I/O serialised by one lock), `WordDriverBase` (register protocols, overlap check, block splitting), `ModbusTcpDriver` (NModbus), `OmronFinsDriver` (own UDP + TCP client, §9.3 framing, SID matching, retries), `OpcUaDriver`, `SimulatorDriver`, `DriverFactory`, `NetworkHint`, `PlcTelemetryService`, `PlcIoMap`.
- **Data:** `CsvLogger`.
- **Core:** `TelemetrySnapshot`, `Alert`, `AlertSeverity`, `AlertSources`.
- **Physics fix:** `TransferFunctions.Piston` / `ElevatedHinge` returned NaN for kh ≳ 355 (`sinh` overflow). Both now use the algebraically identical form divided by sinh·cosh; Phase 1 closed-form tests still pass, and new tests cover deep water and the spec formula for h0 > 0.

### 3. Packages
- `WaveFlume.Plc`: NModbus 3.0.81, OPCFoundation.NetStandard.Opc.Ua.Client 1.5.374.176 (pinned to the 1.5 line; 2.x has a different API)
- `WaveFlume.Plc.Tests`: Microsoft.Extensions.TimeProvider.Testing 8.10.0, FluentAssertions 6.12.0

### 4. Build / test
`dotnet build WaveFlume.sln` → 0 warnings. `dotnet test WaveFlume.sln` → 112 passed (Physics 68, Plc 41, Data 3), stable over repeated runs.

### 5. Acceptance checklist
- [x] Word-order encoding and FINS read/write/response/FINS-TCP frames against hand-built byte arrays
- [x] FINS driver (UDP and TCP) end-to-end against a loopback fake PLC: recipe, commands, heartbeat, telemetry, full trajectory upload with PLC-side CRC/sequence check
- [x] FINS retries dropped frames, then faults with Timeout; wrong node (end code 0x0101) reported as WrongNode
- [x] Modbus driver against an NModbus TCP slave; refused port reported as Refused
- [x] Chunking: sequence, CRC-16/MODBUS, padding, `MaxBlockWords`; simulator rejects gaps and bad CRC
- [x] Heartbeat loss → Smooth Stop + `CommsLoss` in the simulator; latched until Reset
- [x] PC side: frozen PLC heartbeat → Critical alert + Smooth Stop; paddle > 95 % clamp → Smooth Stop; hard fault → E-stop
- [x] Simulator: servo 2nd-order lag with rate/stroke clamp; probes = linear Biesel transfer + propagation; golden wave gives η ≈ H/2 at 5 m and X ≈ 13.6 mm
- [x] Block telemetry: counter-based, lost samples counted when the ring overflows
- [x] CSV: header, columns, 10 min of 200 Hz × 17 columns written far faster than real time
- [x] Connect refused while the TagMap fails validation; shipped profiles validate
- [x] `docs/PLC_IO_MAP.md` generated from profiles; a test fails if it drifts
- [ ] OPC UA driver compiles but is **not tested against a server** (no in-process server yet). Test against the real PLC or an OPC UA simulator before use.
- [ ] `PlcTelemetryService` timing thread (Stopwatch + `timeBeginPeriod(1)`) is not covered by tests — tests drive `TickAsync` directly. Jitter will be measured in Phase 3 against the simulator.

### 6. Decisions and open questions
- **Signals added to §9.1:** `HeartbeatPc` (heartbeat must flow both ways, §9.4) and `ChunkCrc` (§9.2 asks for a checksum but lists no signal). `IPlcDriver` gained `SendHeartbeatAsync`, `State`, `CommErrorCount`, `LastError` (§9.4 asks for the counter and last error in the UI).
- **Trajectory mode reuses `Cycles` as total samples per paddle**, written with chunk 1. The last chunk is padded by repeating its final sample. `BufferLevel` = free samples in the PLC ring.
- **CRC** is over float32 big-endian bytes, independent of wire word order, so the PLC programmer has one rule.
- **BOOL takes a whole register** in Modbus/FINS (no bit addressing).
- **Omron profile uses CDAB** word order (Omron REAL low word first). Confirm with the actual PLC.
- **Block telemetry** works only in the simulator. Real drivers throw `NotSupportedException` until the PLC ring-buffer layout is decided (open input §16); the service then uses poll mode, which means 6 transactions per sample — merge adjacent tags if 125 Hz polling can't keep up.
- **Connect timeout is at least 3 s** because Windows retries a SYN for ~2 s after an RST; request timeouts still use the configured value. `PlcEndpoint.TimeoutMs` defaults to 1000 ms; the Phase 3 Settings panel must default FINS to 100 ms per §9.3.
- **OPC UA:** security None auto-accepts the server certificate; Sign/SignAndEncrypt require it in `pki/trusted`. Passwords are plain in `PlcEndpoint.Extras` for now — DPAPI arrives with the Settings UI (Phase 3).
- **Simulator simplifications:** probe model uses the paddle-average motion (2-D, no oblique/side-wall effects), no reflection or evanescent modes; servo response is precomputed at Start, so a Smooth Stop scales the precomputed motion by the ramp-down; trajectory uploads are refused while running (no streaming).
- **Runtime guard not yet done:** following error (needs commanded vs actual) and probe 4–20 mA fault (needs raw mA, comes with calibration in Phase 5).

## Phase 3 — WPF UI — 2026-10-30

### 1. Tree
See `docs/README.md` → Layout. New: `src/WaveFlume.App` (assembly `WaveFlume.exe`), `tests/WaveFlume.App.Tests`, `docs/UI_STYLE_GUIDE.md`, `docs/screenshots/` (10 PNGs: 4 tabs + the probe scope view, light and dark).

### 2. Source
- **Shell:** `App` (generic host + DI, Serilog rolling files, theme switching, `--screenshots` mode), `MainWindow` (header with E-STOP, alarm banner, Wave Generation / Wave Paddle / Settings / Help tabs, alarm/event log, permanent status bar).
- **Themes:** `Light.xaml`, `Dark.xaml` (same keys), `Controls.xaml` (flat restyles of Button, TextBox, PasswordBox, ComboBox, GroupBox, TabControl, DataGrid, ProgressBar, ToolTip). No third-party UI kit.
- **View-models:**
  - `WaveGenerationViewModel`: inputs, Calculate on a worker thread, gatekeeper display, recipe CRUD.
  - `WavePaddleViewModel`: state-gated commands, trajectory upload with progress, run lifecycle with a per-run CSV and extra record time, live 60 s and final zero-upcrossing statistics, paddle indicators.
  - `SettingsViewModel`: admin login, limits, ranges, controller connection with per-protocol extras, Test Connection, network hint, TagMap editor, DB fields.
  - `MainViewModel`: alarm banner, status bar and the UI tick.
- **Services:**
  - `AppState`.
  - `SettingsStore`: JSON; ceilings validated on save; profiles copied to a writable folder.
  - `Secrets`: PBKDF2-SHA256 admin hash, DPAPI for the OPC UA and DB passwords.
  - `RecipeStore`: local JSON, kept as the offline fallback in Phase 5.
  - `PlcSession`: driver + telemetry; `RunState` from the StatusWord; a throw-away driver for Test Connection.
  - `AlarmService`.
- **Core/Plc/Data/Physics additions:** `RunState` + `RunStateRules`, `WaveRecipe`, random/focusing fields on `WaveRequest` (used in Phase 4), `StatusMapping`, `TestDataFile`, `TrajectoryGenerator.PredictedElevation`.
- **Phase 2 fixes found while building the UI:**
  - The telemetry CSV channel is now created per run (`BeginCsvCapture` / `EndCsvCapture`). A permanent bounded channel nobody read would have stalled the telemetry thread between runs.
  - A driver no longer stays in `Fault` after one failed command once a later transaction succeeds.
  - Simulator water depth is settable per recipe.
  - Default block read rate is 50 Hz (was 25 Hz, which capped the scopes below 30 FPS).
- **UI bugs found from the screenshots and fixed:**
  - A theme change invalidated the calculation.
  - Plots did not restyle after their tab had been unloaded.
  - The TagMap grid columns collapsed inside a horizontally scrolling panel.
  - The board plot passed 8 buffers for 6 paddles.
  - A corrupted protected secret threw instead of reading as unset.

### 3. Packages (`WaveFlume.App`)
CommunityToolkit.Mvvm 8.3.2, Microsoft.Extensions.Hosting 8.0.1, Serilog.Extensions.Hosting 8.0.0, Serilog.Sinks.File 6.0.0, ScottPlot.WPF 5.0.56 (5.1.x pulls a .NET-Framework-only SkiaSharp WPF build), System.Security.Cryptography.ProtectedData 8.0.0.

### 4. Build / run / test
`dotnet build WaveFlume.sln` → 0 warnings. `dotnet test WaveFlume.sln` → 135 passed (Physics 68, Plc 41, Data 3, App 23). Run: `dotnet run --project src/WaveFlume.App`.

### 5. Acceptance checklist
- [x] Light and dark industrial themes; every tab captured in both (`docs/screenshots/`) and checked for legibility
- [x] State machine gates every button; E-STOP always visible and always enabled
- [x] Calculate → gatekeeper result in red (BLOCKED) / amber (warnings) / green (OK) with a text cue; first violation shown, all violations logged as alarms
- [x] Any input or flume-limit change invalidates the calculation, so a stale trajectory can never be sent; a theme change does not
- [x] Send disabled while a_max is unset; the status bar shows "Acceleration limit not configured"
- [x] Limit tab: curve, operating point green inside / red outside, hover shows the active limit; Wave Board, Wave Elevation and Spectrum previews
- [x] Full run on the simulator through the view-models: connect → enable → home → send (progress) → start → smooth stop → CSV with header → final statistics (automated test)
- [x] Six paddle bars with WARN at 80 % / ALARM at 95 %; live (60 s, 1 Hz) and final statistics
- [x] Oscilloscopes: board motion, 3 stacked probe charts with channel selectors, 10/30/60 s window, pause, time series / spectrum, Open Test Data (offline CSV)
- [x] Measured on the simulator (1920 × 1055 window): scope 30–37 FPS; telemetry thread 0 missed cycles, 0 lost samples, max jitter 0.6–15 ms at 50 Hz block reads
- [x] Recipe CRUD (Save / Add / Update / Load Prev Wavegen / New / Delete) on local JSON
- [x] Settings: admin login (PBKDF2), hard ceilings cannot be saved, per-protocol extras, IPv4 validation, network hint, Test Connection with reason and RTT, TagMap editor with Validate / Import / Export, DPAPI-protected passwords; Connect refused while the TagMap is invalid
- [x] Confirmations: start above 80 % of the clamp, % modification > 100, closing during a run, deleting a recipe, setting the first admin password
- [ ] 60 FPS goal not reached: ScottPlot software rendering of 3 × 30 s traces at full width gives 30–37 FPS
- [ ] MySQL test button disabled (Phase 5); recipes are local JSON until then
- [ ] Not tested against real PLC hardware or a real OPC UA server

### 6. Decisions and open questions
- **Default protocol is the Simulator**, not the spec's `Omron_CIP` (EtherNet/IP is Phase 5 and there is no PLC yet).
- **Recipe buttons:** the spec names Save, Add, Update, Load Prev Wavegen and New without defining them. They are implemented as:
  - **Save:** insert under the typed test number, which must be new.
  - **Add:** insert under the next free `T-0001`-style number.
  - **Update:** overwrite the recipe with this test number.
  - **Load Prev Wavegen:** load the selected recipe's inputs; recalculate before sending.
  - **New:** reset the inputs.
  - **Delete:** added for CRUD.

  Please confirm against the legacy behaviour.
- **"PLC Ethernet connection" toggle** is the Connect/Disconnect button; the **DAQ/probe stream** checkbox pauses the probe plots and live statistics. "Assistant diagnostics" (§11.3) is unspecified and not built.
- **Save Test Data** copies the run CSV (already written live) to a chosen location; DB and PDF records come in Phase 5.
- **Trajectory mode is used for every wave type.** Parametric mode stays available in the PLC layer but has no UI switch.
- **Theme and oscilloscope style** are viewing preferences: an operator can change them without admin login.
- **Live statistics count noise crossings on calm water** (§7.12 has no minimum wave-height threshold). Add a threshold or low-pass filter if operators find this confusing.
- **OPC UA licence:** OPCFoundation.NetStandard is dual-licensed GPL-2.0 / RCL — check before distributing the installer (listed on the Help tab).

## Phase 4 — All wave types — 2026-10-30

### 1. Tree
New in `src/WaveFlume.Physics`: `Spectra`, `SpectralSynthesis`, `IrregularWaves`, `SolitaryWave` (+ `SecondOrder`), `UserSeries`. New tests: `SpectraTests`, `WaveTypeTests` (Physics) and `EndToEndWaveTests` (Plc, through the simulator). Screenshots `Light_5…8` show random, focusing and solitary waves.

### 2. Source
- **Spectra (§7.7):** PM, generalised PM (β/4 coefficient), JONSWAP (σ 0.07/0.09; α found by normalisation), TMA (φ(ω_h) × JONSWAP), and Darbyshire behind a flag. Every shape is normalised to m0 = Hs²/16 on a 40 000-point trapezoid grid.
- **Random (§7.6):**
  - A_i = √(2 S(f_i) Δf), with phases from `System.Random(seed)` in ascending frequency.
  - Band [f_lo, f_hi] = [max(1/T_max, 0.4 f_p), min(1/T_min_eff, 4 f_p)]; both factors live in `flume_config.json`. The % of energy removed is reported.
  - Path A (paddle spectrum S/c0 → inverse FFT) and Path B (η(t) → FFT → ÷c0 → inverse FFT) are both available, and a test shows they give the same paddle motion.
  - Synthesis runs one paddle at a time, stored as float32. Oblique phase φ = −k y sin θ is applied per component in the frequency domain.
  - "Scale to target Hs" rescales the realisation so its zero-crossing H1/3 equals the target. The realised H1/3 and Hm0 are reported either way.
- **Focusing:** amplitudes follow the chosen spectral shape, with Σ a_i = H/2; phases φ_i = −k_i x_f + ω_i t_f. t_f must lie between the ramps.
- **Solitary (Goring 1978):** κ = √(3H/4h³), c = √(g(h+H)); dX/dt = c·η/(h+η) is integrated with RK4 (4 substeps per sample). The total stroke is reported.
- **User-defined wave:** CSV η(t) → ramp → zero-padded FFT → band-limit to [1/T_max, 1/T_min_eff] → ÷c0 → inverse FFT. **User-defined stroke:** CSV X(t), 1 column or one per paddle, used directly (% modification only) and still checked by the gatekeeper.
- **Second order:**
  - Madsen (1971) for monochromatic + piston, exactly as §7.4, ramped with E². The Stokes bound harmonic is added to the predicted elevation.
  - Bichromatic, random and focusing in 2nd order throw a clear "needs Schäffer F±/G±" message.
- **Engine and gatekeeper:**
  - Generators return design waves (height range, breaking, Ursell/kh) separately from spectral components (period range, fs, phase step). Each issue code is reported once, so a 30 000-component sea doesn't produce 30 000 messages.
  - New warning when a trajectory exceeds `WarnSamplesPerPaddle`.
  - `WaveResult` now carries the predicted η(t), the target spectrum, random-wave info and info lines.
  - Wavemaker outputs include % signal modification. Non-regular types report the exact trajectory maxima, and fields that don't apply stay blank.
- **UI:**
  - The Spectrum panel is shared by random and focusing waves and includes the synthesis-path selector. New user-file panel with Browse.
  - Fields that don't apply to the selected type are disabled. Info lines appear under the gatekeeper result.
  - The Spectrum tab overlays the target S(f) on the predicted-elevation PSD.

### 3. Packages
None added.

### 4. Build / test
`dotnet build WaveFlume.sln` → 0 warnings. `dotnet test WaveFlume.sln` → 167 passed (Physics 98, Plc 43, Data 3, App 23).

### 5. Acceptance checklist (§14)
- [x] Every spectrum integrates to Hs²/16; the PM closed form matches; JONSWAP(γ = 1) = PM; TMA = JONSWAP where ω_h > 2; generalised PM normalisation is correct for β ∈ {0.8, 1.25, 2}
- [x] Same seed → identical SHA-256 of the trajectory; a different seed gives a different one
- [x] Band-limit energy report matches an independent integral; realised Hm0 = Hs·√(1 − removed)
- [x] "Scale to target Hs" hits the target H1/3 to 1e-9; unscaled scatter is within ±15 %
- [x] Path A ≡ Path B; the single-bin FFT sign/phase convention is tested against cos/sin directly
- [x] **Independent end-to-end checks through the simulator** (Phase 2 Biesel + propagation + servo model):
  - Focused group peaks at x_f within 0.3 s of t_f, at 0.85–1.05 × H/2.
  - Random sea at 5 m has Hm0 within 6 % of the realised value, with Tp ≈ 1.5 s.
- [x] Solitary: X(0) = 0, monotonic, stroke within 10 % of the small-amplitude limit √(16Hh/3), peak velocity c·H/(h+H)
- [x] User wave: steady amplitude a/c0, detected Tp; user stroke reproduced exactly with % modification; bad column count rejected
- [x] Madsen golden value −0.130 mm (hand-calculated); trajectory = X1 sin ωt + X2 sin 2ωt mid-run; deep-water Stokes limit k a²/2
- [ ] Cnoidal — **not implemented** (no formulas in the spec; see TODO_VERIFY #7)
- [ ] Schäffer F±/G± — **not implemented** (TODO_VERIFY #3)
- [ ] Madsen sign/phase and the "no free 2nd harmonic" check — **not verified** (TODO_VERIFY #2)

### 6. Decisions and open questions
- **FFT length is the next power of two ≥ the record** (MathNet uses radix-2 only for powers of two; other lengths fall back to Bluestein at about 3 × 2N complex buffers, roughly 800 MB at the spec's 10 000-wave worst case). So Δf = fs/L ≤ 1/D and the repeat period L/fs ≥ D. The run still lasts exactly N_waves·T_p. The UI and info line state both values. The ramps at both ends make the truncation harmless.
- **Amplitudes are deterministic** (A_i = √(2SΔf), random phases only, as §7.6 writes). The ±10 % scatter mentioned in §7.6 shows up in the zero-crossing H1/3, which is what "scale to target Hs" corrects.
- **Focusing amplitude:** §7.5 says "amplitude" without defining it. The Signal Wave Height field is used as the focused height, with linear crest = H/2, and the amplitudes follow the selected spectral shape over the same band. Please confirm.
- **Solitary paddle ends displaced** by the full stroke, because X(0) = 0 is required. That limits H to about 0.035 m at h = 0.6 m with the ±350 mm clamp. Starting at −S/2 would double the reach but breaks the X(0) = 0 rule — your call.
- **Solitary breaking** is not checked, since §7 has no solitary breaking formula. The height range and the trajectory limits still apply.
- **User wave band** uses [1/T_max, 1/T_min_eff] because T_p is unknown before the FFT; T_p is then taken from the spectrum peak for the kinematics panel.
- **Force for irregular waves** uses the design wave (Hs or H at Tp), and power uses Σ over components. Both are estimates.
- New open inputs: **cnoidal reference formulas** (TODO_VERIFY #7), in addition to the Schäffer closed forms.

## Phase 5 — Database, calibration, report, EtherNet/IP + S7, publish — 2026-10-30

### 1. Tree
New: `sql/schema.sql`, `src/WaveFlume.Reporting` (PdfReportService, Plots), `Data/DatabaseService.cs`, `Data/ProbeCalibration.cs`, `Plc/EtherNetIpDriver.cs`, `Plc/S7Driver.cs`, profiles `omron_eip_nx.json` + `siemens_s7_db.json`, App `DataSession`/`RecipeRepository`/`DbMapping`, `CalibrationViewModel`/`CalibrationView`, `installer/WaveFlume.iss` + `build-release.ps1`.

### 2. Source
- **Schema:** §12 verbatim, plus `IF NOT EXISTS`, so "Create Schema" can be re-run.
- **DatabaseService (MySqlConnector):**
  - Async and parameterised only; recipe upsert + run insert in one transaction.
  - Retries transient errors (3 attempts) and keeps an `IsOnline` flag.
  - Every session uses `time_zone = '+00:00'`, so column defaults are UTC too.
  - Duplicate test number and RESTRICT violations become clear messages.
- **App integration:**
  - The database is **off until enabled** in Settings.
  - Recipes go to MySQL and fall back to local JSON when it is unreachable.
  - Alerts are written by a background queue tagged with the current `run_id`.
  - `test_runs` gets a row at Start (status, limits/profile/version snapshot, CSV path) and is finished with status Completed / E-Stopped / Fault and statistics, then the PDF path.
  - Settings save mirrors `system_settings` (the DB password is never in it).
  - Status bar shows `DB: disabled / online / offline`.
- **Probe calibration (§10):**
  - Least-squares η = gain·mA + offset with R² and residuals; the wizard captures 2 s averages per dip point; saved (admin) to `calibration.json`.
  - Applied live by the telemetry service; uncalibrated channels use the nominal range in config.
  - **Loop-fault guard** on active channels (< 3.8 mA or > 20.5 mA → Critical WaveProbe alarm + Smooth Stop, latched).
  - The simulator now emits raw mA through the nominal mapping, so calibration can be exercised without hardware.
  - CSV header and PDF carry the calibration snapshot.
- **PDF (QuestPDF + ScottPlot PNG):**
  - A4, multi-page: header (title, logo slot, run/test, time), recipe, recalculated kinematics, limit utilisation (commanded and measured stroke, velocity, acceleration vs limits), paddle and elevation time series, Welch spectra, per-probe statistics (H1/3, H10, Hmax, T1/3, T10, Tmax, Hm0, Tp), alerts, "Page x of y".
  - Generated off the UI thread with progress after every run, and on demand ("PDF Report", also for an opened CSV).
- **EtherNet/IP (libplctag):** Omron NX/NJ (default) or ControlLogix.
  - Tags are cached per address; BOOL/UINT/DINT/REAL sizes are handled.
  - Controller- and program-scoped tag names are validated.
  - libplctag status codes map to Timeout / Refused / InvalidConfig / AccessDenied.
- **S7 (S7netplus):**
  - `DB10.DBD4` / `DBW2` / `DBX0.0` addressing, with the type checked against the address kind and overlaps detected within each DB.
  - Big-endian codec through the existing `WordCodec`; BOOL written with WriteBit.
  - Rack, slot and CPU type come from the endpoint extras.
- **Publish:**
  - The spec's `dotnet publish` command produces a 216 MB self-contained `WaveFlume.exe`.
  - `--extract-native` writes `plctag.dll`, which the installer ships. libplctag.NET otherwise re-extracts it on every start, which fails in read-only Program Files; the driver turns that off when the DLL is present.
  - Inno Setup script provided, with no firewall or NIC changes by design.

### 3. Packages
MySqlConnector 2.6.2 (2.4/2.5 carry advisory GHSA-473q-m89c-ghf8), QuestPDF 2024.12.3, ScottPlot 5.0.56 (Reporting), libplctag 1.5.2, S7netplus 0.20.0.

### 4. Build / test / release
`dotnet build WaveFlume.sln` → 0 warnings. `dotnet test WaveFlume.sln` → 197 passed, **4 skipped** (live MySQL, need a test database — see below). `powershell -File installer/build-release.ps1` → tests, publish, plctag.dll, installer if ISCC is present.

### 5. Acceptance checklist
- [x] Schema matches §12; DatabaseService for all four tables; offline fallback; UTC timestamps
- [x] Offline DB is reported, not thrown (test); recipe commands fall back to JSON
- [ ] **Live MySQL repository tests written but not yet run.** They cover CRUD with every field, duplicate and RESTRICT errors, run lifecycle with ms timestamps, alerts with run_id, the single-row settings upsert, and idempotent schema. They need the `waveflume_test` user (instructions given in chat).
- [x] Calibration fit (exact line, R², residuals), ≥ 3 points and degenerate input rejected, nominal fallback, persistence, wizard admin gating
- [x] Loop fault → Critical + Smooth Stop, active channels only, latched; raw → m conversion applied in telemetry
- [x] PDF generated from a run CSV (valid `%PDF-`, plots embedded); the end-to-end simulator run produces its PDF automatically
- [x] S7 address parser, type/kind checks, overlap, big-endian codec; closed-port connects fail cleanly for S7 and EtherNet/IP (native plctag.dll loads)
- [x] Published single-file exe runs end to end on the simulator (14 screenshots, 32 FPS, 0 lost samples)
- [ ] Installer not compiled here (Inno Setup not installed, by choice). Script and release script provided.
- [ ] EtherNet/IP, S7, FINS, Modbus and OPC UA never tested against real PLCs (no hardware)

### 6. Decisions and open questions
- **`Probe_m` now carries mA**, not metres: the TagMap scale converts PLC counts to mA and the app's calibration converts mA to m (§10). Profiles and `PLC_IO_MAP.md` say so. The simulator emits mA through the nominal range. If your PLC already scales to metres, set the TagMap scale to (16 mA / span) and offset to match, or tell me to add a "metres from PLC" mode.
- **Active probe count = 3** and the nominal range ±0.25 m for 4–20 mA are placeholders for the open inputs (number, positions and ranges of probes).
- **Local settings stay authoritative;** `system_settings` is a write-through mirror. The DB is not read back at start-up because the local file must work offline. Tell me if the DB should win when online.
- **Recipes saved while offline stay in local JSON** and are not synced to MySQL later.
- **Default protocol is still the Simulator.** The EtherNet/IP driver now exists for the spec's Omron default, but connecting without the PLC would just fail. Switch in Settings on the flume PC.
- **EtherNet/IP path** defaults to `1,0` (extra `EipPath`). For Omron NX/NJ some firmware wants a different path; verify on the PLC.
- **QuestPDF Community licence:** free below USD 1M revenue, non-profit or academic. The institute should confirm before distribution.
