# WaveFlume — 6-paddle wavemaker SCADA/HMI (.NET 8)

Upgrade of IITM WaveGen v1.1. Full specification: `../WAVE_FLUME_DOTNET_MASTER_PROMPT.md`.

## Build and test

Requires the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

```bash
dotnet build WaveFlume.sln
```

```bash
dotnet test WaveFlume.sln
```

`Directory.Build.props` sets C# 12, nullable, and warnings-as-errors for every project.

## Run the desktop app

```bash
dotnet run --project src/WaveFlume.App
```

Windows only (WPF). The first run uses the **Simulator** (virtual flume), so no PLC is needed. Settings live in
`%LOCALAPPDATA%\WaveFlume\settings.json` (TagMap profiles in `plc_profiles\` beside it, logs in `logs\`);
recipes and run CSVs go to `Documents\WaveFlume\`.

Before anything can be sent: Settings → **Login to Edit** (the first login sets the admin password) → enter
**Max Acceleration** → Save. Then Wave Generation → Calculate → Wave Paddle → Connect, Enable Servos, Home,
Send to Wave Paddle, Start Wave.

### Database (optional)

MySQL 8.0.16+ (tested schema on 9.7). In Settings (admin): fill Host/Port/Database/User/Password, tick
**Use the database**, **Test DB**, then **Create Schema** (runs `sql/schema.sql`; safe to repeat). Recipes, runs,
alerts and a mirror of the settings then go to MySQL; when it is unreachable the app falls back to local JSON
recipes and CSV-only runs, and the status bar shows `DB: offline`. The DB password is DPAPI-protected in the local
settings file and never stored in the database. Telemetry never goes to MySQL.

Live repository tests run when `WAVEFLUME_TEST_MYSQL` or `%LOCALAPPDATA%\WaveFlume\test-mysql.txt` holds a
connection string to a disposable test database (they drop and recreate the four tables there).

### Probe calibration

`Probe_m` in the TagMap carries loop current in mA. Settings → Channels and Calibrations: dip a probe at ≥ 3 known
elevations, **Capture point** at each, **Fit** (least squares, R² and residuals), **Save** (admin). Uncalibrated
channels use the nominal range in `flume_config.json` → `Probes`. Below 3.8 mA or above 20.5 mA on an active channel
raises a Critical alarm and commands Smooth Stop.

### Reports

Every completed run gets `Runs\<run>.pdf` next to its CSV (recipe, kinematics, limit utilisation, paddle and
elevation plots, measured spectrum, statistics per probe, alerts). **PDF Report** on the Wave Paddle tab rebuilds it,
or reports on an opened test-data CSV. QuestPDF is used under its Community licence: free for organisations below
USD 1M annual revenue, non-profits and academic use — confirm eligibility before distribution.

## Release build

```bash
powershell -File installer/build-release.ps1
```

Runs the tests, publishes a self-contained single-file `WaveFlume.exe` (win-x64, ~216 MB, no .NET install needed)
to `artifacts/publish`, extracts `plctag.dll` (libplctag native library, shipped by the installer so the app never
writes into Program Files), and compiles `installer/WaveFlume.iss` when Inno Setup 6 is installed.

Screenshots of every tab in both themes (`docs/screenshots/`) are regenerated with:

```bash
dotnet run --project src/WaveFlume.App -- --screenshots docs/screenshots-new
```

That runs the golden wave on the simulator with a throw-away settings file inside the target folder.

## Layout (current)

```
WaveFlume.sln
Directory.Build.props
config/flume_config.json           physical flume, limits, ranges (SI units)
config/plc_profiles/*.json         TagMap profiles: Omron FINS (DM), Modbus TCP (HR), OPC UA (NodeIds)
src/WaveFlume.Core/                enums, HardLimits, Units, FlumeConfig, WaveRequest, Trajectory, SafetyReport,
                                   TelemetrySnapshot, Alert
src/WaveFlume.Physics/             Dispersion, TransferFunctions, Ramp, WaveComponent + OrientationPhase,
                                   TrajectoryGenerator, SafetyGatekeeper, LimitCurve, WaveStatistics, WaveMathEngine
src/WaveFlume.Plc/                 IPlcDriver + records, TagMap, WordCodec, TrajectoryChunker + Crc16,
                                   TagMapDriverBase/WordDriverBase, ModbusTcp/OmronFins/OpcUa/EtherNetIp/S7 drivers,
                                   SimulatorDriver, DriverFactory + NetworkHint, PlcTelemetryService, PlcIoMap
src/WaveFlume.Data/                CsvLogger, TestDataFile (CSV reader), DatabaseService (MySQL), ProbeCalibration
src/WaveFlume.Reporting/           PdfReportService (QuestPDF + ScottPlot PNGs)
sql/schema.sql                     MySQL schema (spec §12)
installer/                         WaveFlume.iss (Inno Setup), build-release.ps1
src/WaveFlume.App/                 WPF: Themes (Light/Dark/Controls), Views, ViewModels, Services
                                   (AppState, SettingsStore + DPAPI/PBKDF2, RecipeStore, PlcSession, AlarmService)
tests/WaveFlume.*.Tests/           xUnit + FluentAssertions (Plc tests use loopback fake FINS/Modbus servers;
                                   App tests drive the view-models against the simulator)
docs/                              README, TODO_VERIFY, PHASE_REPORTS, PLC_IO_MAP (generated), UI_STYLE_GUIDE, screenshots/
```

`docs/PLC_IO_MAP.md` is generated from the profiles; after editing a profile run:

```bash
WAVEFLUME_REGEN_DOCS=1 dotnet test WaveFlume.sln
```

## Running against the virtual flume

```csharp
await using var plc = DriverFactory.Create(PlcProtocolType.Simulator, null);
await plc.ConnectAsync(new PlcEndpoint(), ct);
var telemetry = new PlcTelemetryService(plc, new TelemetryOptions());
telemetry.Start(new PlcEndpoint());
_ = CsvLogger.WriteAsync(telemetry.BeginCsvCapture(), "Runs/" + CsvLogger.RunFileName(DateTime.UtcNow, "T-01"), header, 6, 3, ct);
```

## Usage

```csharp
var cfg = FlumeConfig.Load("config/flume_config.json");
var result = new WaveMathEngine(cfg).Calculate(new WaveRequest { WaterDepthM = 0.6, WaveHeightM = 0.05, PeriodS = 1.0 });
if (!result.Safety.Ok) { /* show result.Safety.Violations[0].Message in red; nothing is sent */ }
```

`MaxAccelMs2` is `null` in the shipped config, so every calculation reports `ACCEL_NOT_CONFIGURED` until an admin sets it (spec §2).

## Safety note

The PC is supervisory and non-real-time. The hardwired E-stop chain is the primary stop and the PLC must enforce the same stroke, velocity and acceleration limits independently.
