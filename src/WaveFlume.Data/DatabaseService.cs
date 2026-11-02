using System.Data;
using System.Text.Json;
using MySqlConnector;
using WaveFlume.Core;

namespace WaveFlume.Data;

public enum RunStatus { Running, Completed, EStopped, Fault }

public sealed record RunStatistics(double? H13M, double? H10M, double? HmaxM, double? T13S, double? T10S, double? TmaxS);

public sealed record TestRunRecord
{
    public int RunId { get; init; }
    public required string TestNumber { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime? EndUtc { get; init; }
    public double ExtraRecordS { get; init; }
    public RunStatus Status { get; init; }
    public RunStatistics? Statistics { get; init; }
    public string? LimitsSnapshotJson { get; init; }
    public required string CsvPath { get; init; }
    public string? PdfPath { get; init; }
}

/// <summary>system_settings row in SI/DB units as stored (mm for stroke/velocity/acceleration).</summary>
public sealed record SystemSettingsRow(
    PaddleType PaddleType, double HingeHeightM, double MaxStrokeMm, double MaxVelocityMms, double? MaxAccelMms2,
    double PaddleWidthM, int NumPaddles, double MaxWaterDepthM, double TMinS, double TMaxS,
    double CommandFsHz, double TelemetryRateHz, string PlcProtocol, string PlcIpAddress, int PlcPort,
    string? PlcExtrasJson, string? TagMapProfile, string? DaqIpAddress, double CalibrationFactor);

/// <summary>
/// MySQL access (spec §12): async, parameterised only, transactions for multi-statement work, retry on
/// transient errors, and an <see cref="IsOnline"/> flag the UI uses for its offline fallback.
/// High-rate telemetry never comes here — it goes to CSV.
/// </summary>
public sealed class DatabaseService(string connectionString)
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    public bool IsOnline { get; private set; }
    public string? LastError { get; private set; }

    public static string BuildConnectionString(string host, int port, string database, string user, string? password) =>
        new MySqlConnectionStringBuilder
        {
            Server = host, Port = (uint)port, Database = database, UserID = user, Password = password ?? "",
            ConnectionTimeout = 5, DefaultCommandTimeout = 15, AllowUserVariables = false,
        }.ConnectionString;

    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        try
        {
            var version = await RunAsync(async c => (string)(await Cmd(c, "SELECT VERSION()").ExecuteScalarAsync(ct))!, ct);
            return (true, $"Connected: MySQL {version}");
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Runs sql/schema.sql in the configured database (CREATE DATABASE / USE lines skipped).</summary>
    public Task InitializeSchemaAsync(string script, CancellationToken ct) => RunAsync(async c =>
    {
        var statements = string.Join('\n', script.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("--") && !l.TrimStart().StartsWith("CREATE DATABASE", StringComparison.OrdinalIgnoreCase)
                            && !l.TrimStart().StartsWith("USE ", StringComparison.OrdinalIgnoreCase)))
            .Split(';').Select(s => s.Trim()).Where(s => s.Length > 0);
        foreach (var s in statements) await Cmd(c, s).ExecuteNonQueryAsync(ct);
        return 0;
    }, ct);

    // ---- system_settings --------------------------------------------------------------------

    public Task SaveSystemSettingsAsync(SystemSettingsRow s, CancellationToken ct) => RunAsync(async c =>
    {
        const string sql = """
            INSERT INTO system_settings (setting_id, paddle_type, hinge_height_m, max_stroke_mm, max_velocity_mms, max_accel_mms2,
              paddle_width_m, num_paddles, max_water_depth_m, t_min_s, t_max_s, command_fs_hz, telemetry_rate_hz,
              plc_protocol, plc_ip_address, plc_port, plc_extras_json, tagmap_profile, daq_ip_address, calibration_factor)
            VALUES (1, @pt, @h0, @stroke, @vel, @acc, @pw, @np, @depth, @tmin, @tmax, @fs, @tr, @proto, @ip, @port, @extras, @profile, @daq, @cal)
            ON DUPLICATE KEY UPDATE paddle_type=VALUES(paddle_type), hinge_height_m=VALUES(hinge_height_m), max_stroke_mm=VALUES(max_stroke_mm),
              max_velocity_mms=VALUES(max_velocity_mms), max_accel_mms2=VALUES(max_accel_mms2), paddle_width_m=VALUES(paddle_width_m),
              num_paddles=VALUES(num_paddles), max_water_depth_m=VALUES(max_water_depth_m), t_min_s=VALUES(t_min_s), t_max_s=VALUES(t_max_s),
              command_fs_hz=VALUES(command_fs_hz), telemetry_rate_hz=VALUES(telemetry_rate_hz), plc_protocol=VALUES(plc_protocol),
              plc_ip_address=VALUES(plc_ip_address), plc_port=VALUES(plc_port), plc_extras_json=VALUES(plc_extras_json),
              tagmap_profile=VALUES(tagmap_profile), daq_ip_address=VALUES(daq_ip_address), calibration_factor=VALUES(calibration_factor)
            """;
        await Cmd(c, sql,
            ("@pt", s.PaddleType.ToString()), ("@h0", s.HingeHeightM), ("@stroke", s.MaxStrokeMm), ("@vel", s.MaxVelocityMms),
            ("@acc", s.MaxAccelMms2), ("@pw", s.PaddleWidthM), ("@np", s.NumPaddles), ("@depth", s.MaxWaterDepthM),
            ("@tmin", s.TMinS), ("@tmax", s.TMaxS), ("@fs", s.CommandFsHz), ("@tr", s.TelemetryRateHz), ("@proto", s.PlcProtocol),
            ("@ip", s.PlcIpAddress), ("@port", s.PlcPort), ("@extras", s.PlcExtrasJson), ("@profile", s.TagMapProfile),
            ("@daq", s.DaqIpAddress), ("@cal", s.CalibrationFactor)).ExecuteNonQueryAsync(ct);
        return 0;
    }, ct);

    public Task<SystemSettingsRow?> LoadSystemSettingsAsync(CancellationToken ct) => RunAsync(async c =>
    {
        await using var r = await Cmd(c, "SELECT * FROM system_settings WHERE setting_id = 1").ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SystemSettingsRow(
            Enum.Parse<PaddleType>(r.GetString("paddle_type")), r.GetFloat("hinge_height_m"), r.GetFloat("max_stroke_mm"),
            r.GetFloat("max_velocity_mms"), NullableFloat(r, "max_accel_mms2"), r.GetFloat("paddle_width_m"), r.GetInt32("num_paddles"),
            r.GetFloat("max_water_depth_m"), r.GetFloat("t_min_s"), r.GetFloat("t_max_s"), r.GetFloat("command_fs_hz"),
            r.GetFloat("telemetry_rate_hz"), r.GetString("plc_protocol"), r.GetString("plc_ip_address"), r.GetInt32("plc_port"),
            NullableString(r, "plc_extras_json"), NullableString(r, "tagmap_profile"), NullableString(r, "daq_ip_address"),
            r.GetFloat("calibration_factor"));
    }, ct);

    // ---- wave_recipes -----------------------------------------------------------------------

    public Task<IReadOnlyList<WaveRecipe>> ListRecipesAsync(CancellationToken ct) => RunAsync<IReadOnlyList<WaveRecipe>>(async c =>
    {
        var list = new List<WaveRecipe>();
        await using var r = await Cmd(c, "SELECT * FROM wave_recipes ORDER BY test_number").ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadRecipe(r));
        return list;
    }, ct);

    /// <summary>Insert; a duplicate test number raises <see cref="InvalidOperationException"/>.</summary>
    public Task<int> InsertRecipeAsync(WaveRecipe recipe, CancellationToken ct) => RunAsync(async c =>
    {
        try
        {
            await Cmd(c, InsertRecipeSql, RecipeParams(recipe)).ExecuteNonQueryAsync(ct);
            return Convert.ToInt32(await Cmd(c, "SELECT LAST_INSERT_ID()").ExecuteScalarAsync(ct));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new InvalidOperationException($"Test number '{recipe.TestNumber}' already exists. Use Update.", ex);
        }
    }, ct);

    public Task UpdateRecipeAsync(WaveRecipe recipe, CancellationToken ct) => RunAsync(async c =>
    {
        int rows = await Cmd(c, UpdateRecipeSql, RecipeParams(recipe)).ExecuteNonQueryAsync(ct);
        if (rows == 0 && await RecipeIdAsync(c, recipe.TestNumber, ct) is null)
            throw new InvalidOperationException($"Test number '{recipe.TestNumber}' not found. Use Save.");
        return 0;
    }, ct);

    /// <summary>Insert or update; returns recipe_id (runs reference recipes, so a run always has one).</summary>
    public Task<int> UpsertRecipeAsync(WaveRecipe recipe, CancellationToken ct) => RunAsync(async c =>
    {
        if (await RecipeIdAsync(c, recipe.TestNumber, ct) is { } id)
        {
            await Cmd(c, UpdateRecipeSql, RecipeParams(recipe)).ExecuteNonQueryAsync(ct);
            return id;
        }
        await Cmd(c, InsertRecipeSql, RecipeParams(recipe)).ExecuteNonQueryAsync(ct);
        return Convert.ToInt32(await Cmd(c, "SELECT LAST_INSERT_ID()").ExecuteScalarAsync(ct));
    }, ct);

    /// <summary>Recipes with runs cannot be deleted (ON DELETE RESTRICT keeps run history).</summary>
    public Task DeleteRecipeAsync(string testNumber, CancellationToken ct) => RunAsync(async c =>
    {
        try { await Cmd(c, "DELETE FROM wave_recipes WHERE test_number = @t", ("@t", testNumber)).ExecuteNonQueryAsync(ct); }
        catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.RowIsReferenced2 or MySqlErrorCode.RowIsReferenced)
        {
            throw new InvalidOperationException($"Recipe '{testNumber}' has recorded runs and cannot be deleted.", ex);
        }
        return 0;
    }, ct);

    // ---- test_runs + telemetry_alerts -------------------------------------------------------

    /// <summary>Upserts the recipe and inserts the run in one transaction; returns run_id.</summary>
    public Task<int> StartRunAsync(WaveRecipe recipe, TestRunRecord run, CancellationToken ct) => RunAsync(async c =>
    {
        await using var tx = await c.BeginTransactionAsync(ct);
        int recipeId = await RecipeIdAsync(c, recipe.TestNumber, ct, tx) ?? -1;
        if (recipeId < 0)
        {
            await Cmd(c, InsertRecipeSql, RecipeParams(recipe), tx).ExecuteNonQueryAsync(ct);
            recipeId = Convert.ToInt32(await Cmd(c, "SELECT LAST_INSERT_ID()", tx: tx).ExecuteScalarAsync(ct));
        }
        await Cmd(c, """
            INSERT INTO test_runs (recipe_id, start_timestamp, extra_record_duration_s, run_status, limits_snapshot_json, csv_file_path)
            VALUES (@r, @s, @x, 'Running', @lim, @csv)
            """, [("@r", recipeId), ("@s", run.StartUtc), ("@x", run.ExtraRecordS), ("@lim", run.LimitsSnapshotJson), ("@csv", run.CsvPath)], tx)
            .ExecuteNonQueryAsync(ct);
        int runId = Convert.ToInt32(await Cmd(c, "SELECT LAST_INSERT_ID()", tx: tx).ExecuteScalarAsync(ct));
        await tx.CommitAsync(ct);
        return runId;
    }, ct);

    public Task FinishRunAsync(int runId, RunStatus status, DateTime endUtc, RunStatistics? s, CancellationToken ct) => RunAsync(async c =>
    {
        await Cmd(c, """
            UPDATE test_runs SET end_timestamp=@e, run_status=@st, h_1_3_m=@h13, h_10_m=@h10, h_max_m=@hmax,
              t_1_3_s=@t13, t_10_s=@t10, t_max_s=@tmax WHERE run_id=@id
            """,
            ("@e", endUtc), ("@st", StatusText(status)), ("@h13", s?.H13M), ("@h10", s?.H10M), ("@hmax", s?.HmaxM),
            ("@t13", s?.T13S), ("@t10", s?.T10S), ("@tmax", s?.TmaxS), ("@id", runId)).ExecuteNonQueryAsync(ct);
        return 0;
    }, ct);

    public Task SetRunPdfAsync(int runId, string path, CancellationToken ct) => RunAsync(async c =>
        await Cmd(c, "UPDATE test_runs SET pdf_report_path=@p WHERE run_id=@id", ("@p", path), ("@id", runId)).ExecuteNonQueryAsync(ct), ct);

    public Task<IReadOnlyList<TestRunRecord>> ListRunsAsync(CancellationToken ct) => RunAsync<IReadOnlyList<TestRunRecord>>(async c =>
    {
        var list = new List<TestRunRecord>();
        await using var r = await Cmd(c, """
            SELECT t.*, w.test_number FROM test_runs t JOIN wave_recipes w ON w.recipe_id = t.recipe_id ORDER BY t.run_id DESC
            """).ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new TestRunRecord
            {
                RunId = r.GetInt32("run_id"),
                TestNumber = r.GetString("test_number"),
                StartUtc = DateTime.SpecifyKind(r.GetDateTime("start_timestamp"), DateTimeKind.Utc),
                EndUtc = r.IsDBNull(r.GetOrdinal("end_timestamp")) ? null : DateTime.SpecifyKind(r.GetDateTime("end_timestamp"), DateTimeKind.Utc),
                ExtraRecordS = r.GetFloat("extra_record_duration_s"),
                Status = ParseStatus(r.GetString("run_status")),
                Statistics = new RunStatistics(NullableFloat(r, "h_1_3_m"), NullableFloat(r, "h_10_m"), NullableFloat(r, "h_max_m"),
                    NullableFloat(r, "t_1_3_s"), NullableFloat(r, "t_10_s"), NullableFloat(r, "t_max_s")),
                LimitsSnapshotJson = NullableString(r, "limits_snapshot_json"),
                CsvPath = r.GetString("csv_file_path"),
                PdfPath = NullableString(r, "pdf_report_path"),
            });
        }
        return list;
    }, ct);

    public Task InsertAlertAsync(Alert a, int? runId, CancellationToken ct) => RunAsync(async c =>
        await Cmd(c, """
            INSERT INTO telemetry_alerts (run_id, timestamp, alert_severity, alert_source, description) VALUES (@r, @t, @sev, @src, @d)
            """,
            ("@r", runId), ("@t", a.UtcTime), ("@sev", a.Severity == AlertSeverity.EStop ? "E-Stop" : a.Severity.ToString()),
            ("@src", a.Source), ("@d", a.Description.Length > 500 ? a.Description[..500] : a.Description)).ExecuteNonQueryAsync(ct), ct);

    public Task<IReadOnlyList<Alert>> AlertsForRunAsync(int runId, CancellationToken ct) => RunAsync<IReadOnlyList<Alert>>(async c =>
    {
        var list = new List<Alert>();
        await using var r = await Cmd(c, "SELECT * FROM telemetry_alerts WHERE run_id=@r ORDER BY timestamp", ("@r", runId)).ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sev = r.GetString("alert_severity") switch { "E-Stop" => AlertSeverity.EStop, var s => Enum.Parse<AlertSeverity>(s) };
            list.Add(new Alert(DateTime.SpecifyKind(r.GetDateTime("timestamp"), DateTimeKind.Utc), sev, r.GetString("alert_source"), r.GetString("description")));
        }
        return list;
    }, ct);

    // ---- plumbing ---------------------------------------------------------------------------

    private const string RecipeColumns = """
        test_number, wave_order, wave_type, spectral_shape, random_seed, gamma_factor, beta_factor, water_depth_m,
        signal_wave_height_m, peak_wave_period_s, wave_angle_deg, num_waves, sampling_freq_hz, signal_mod_pct, ramp_time_s,
        extra_params_json, calc_stroke_m, calc_velocity_ms, calc_accel_ms2, calc_force_kn, calc_power_w, calc_wavelength_m, calc_celerity_ms
        """;

    private const string InsertRecipeSql = $"""
        INSERT INTO wave_recipes ({RecipeColumns}) VALUES (@tn, @ord, @type, @shape, @seed, @gamma, @beta, @depth, @h, @t, @ang, @n,
          @fs, @mod, @ramp, @extra, @cs, @cv, @ca, @cf, @cp, @cl, @cc)
        """;

    private const string UpdateRecipeSql = """
        UPDATE wave_recipes SET wave_order=@ord, wave_type=@type, spectral_shape=@shape, random_seed=@seed, gamma_factor=@gamma,
          beta_factor=@beta, water_depth_m=@depth, signal_wave_height_m=@h, peak_wave_period_s=@t, wave_angle_deg=@ang, num_waves=@n,
          sampling_freq_hz=@fs, signal_mod_pct=@mod, ramp_time_s=@ramp, extra_params_json=@extra, calc_stroke_m=@cs,
          calc_velocity_ms=@cv, calc_accel_ms2=@ca, calc_force_kn=@cf, calc_power_w=@cp, calc_wavelength_m=@cl, calc_celerity_ms=@cc
        WHERE test_number=@tn
        """;

    /// <summary>Fields without their own column (bichromatic, focusing, path, user file).</summary>
    private sealed record RecipeExtras(double? Height2M, double? Period2S, double FocusXM, double FocusTimeS, bool ScaleToTargetHs,
        RandomPath Path, string? UserFilePath);

    private static (string, object?)[] RecipeParams(WaveRecipe recipe)
    {
        var q = recipe.Request;
        var extras = new RecipeExtras(q.Height2M, q.Period2S, q.FocusXM, q.FocusTimeS, q.ScaleToTargetHs, q.Path, q.UserFilePath);
        bool spectral = q.Type is WaveType.Random or WaveType.Focusing;
        return
        [
            ("@tn", recipe.TestNumber), ("@ord", (int)q.Order), ("@type", q.Type.ToString()),
            ("@shape", spectral ? q.Spectrum.ToString() : "None"), ("@seed", q.RandomSeed), ("@gamma", q.Gamma), ("@beta", q.Beta),
            ("@depth", q.WaterDepthM), ("@h", q.WaveHeightM), ("@t", q.PeriodS), ("@ang", q.AngleDeg), ("@n", q.NumWaves),
            ("@fs", q.SamplingHz), ("@mod", q.SignalModPct), ("@ramp", q.RampS),
            ("@extra", JsonSerializer.Serialize(extras, FlumeConfig.JsonOptions)),
            ("@cs", recipe.CalcStrokeM), ("@cv", recipe.CalcVelocityMs), ("@ca", recipe.CalcAccelMs2), ("@cf", recipe.CalcForceKn),
            ("@cp", recipe.CalcPowerW), ("@cl", recipe.CalcWaveLengthM), ("@cc", recipe.CalcCelerityMs),
        ];
    }

    private static WaveRecipe ReadRecipe(MySqlDataReader r)
    {
        var x = NullableString(r, "extra_params_json") is { } json
            ? JsonSerializer.Deserialize<RecipeExtras>(json, FlumeConfig.JsonOptions)
            : null;
        string shape = r.GetString("spectral_shape");
        return new WaveRecipe
        {
            TestNumber = r.GetString("test_number"),
            Request = new WaveRequest
            {
                Order = (WaveOrder)r.GetInt32("wave_order"),
                Type = Enum.Parse<WaveType>(r.GetString("wave_type")),
                Spectrum = Enum.TryParse<SpectralShape>(shape, out var sp) ? sp : SpectralShape.Jonswap,
                RandomSeed = r.GetInt32("random_seed"), Gamma = r.GetFloat("gamma_factor"), Beta = r.GetFloat("beta_factor"),
                WaterDepthM = r.GetFloat("water_depth_m"), WaveHeightM = r.GetFloat("signal_wave_height_m"),
                PeriodS = r.GetFloat("peak_wave_period_s"), AngleDeg = r.GetFloat("wave_angle_deg"), NumWaves = r.GetInt32("num_waves"),
                SamplingHz = r.GetFloat("sampling_freq_hz"), SignalModPct = r.GetFloat("signal_mod_pct"), RampS = r.GetFloat("ramp_time_s"),
                Height2M = x?.Height2M, Period2S = x?.Period2S, FocusXM = x?.FocusXM ?? 10, FocusTimeS = x?.FocusTimeS ?? 20,
                ScaleToTargetHs = x?.ScaleToTargetHs ?? false, Path = x?.Path ?? RandomPath.PaddleSpectrum, UserFilePath = x?.UserFilePath,
            },
            CalcStrokeM = NullableFloat(r, "calc_stroke_m"), CalcVelocityMs = NullableFloat(r, "calc_velocity_ms"),
            CalcAccelMs2 = NullableFloat(r, "calc_accel_ms2"), CalcForceKn = NullableFloat(r, "calc_force_kn"),
            CalcPowerW = NullableFloat(r, "calc_power_w"), CalcWaveLengthM = NullableFloat(r, "calc_wavelength_m"),
            CalcCelerityMs = NullableFloat(r, "calc_celerity_ms"),
            CreatedUtc = DateTime.SpecifyKind(r.GetDateTime("created_at"), DateTimeKind.Utc),
        };
    }

    private static async Task<int?> RecipeIdAsync(MySqlConnection c, string testNumber, CancellationToken ct, MySqlTransaction? tx = null) =>
        await Cmd(c, "SELECT recipe_id FROM wave_recipes WHERE test_number=@t", [("@t", testNumber)], tx).ExecuteScalarAsync(ct) is { } v
            ? Convert.ToInt32(v) : null;

    private static string StatusText(RunStatus s) => s == RunStatus.EStopped ? "E-Stopped" : s.ToString();
    private static RunStatus ParseStatus(string s) => s == "E-Stopped" ? RunStatus.EStopped : Enum.Parse<RunStatus>(s);

    private static double? NullableFloat(MySqlDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetFloat(col);
    private static string? NullableString(MySqlDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(col);

    private static MySqlCommand Cmd(MySqlConnection c, string sql, params (string Name, object? Value)[] p) => Cmd(c, sql, p, null);

    private static MySqlCommand Cmd(MySqlConnection c, string sql, (string Name, object? Value)[]? p = null, MySqlTransaction? tx = null)
    {
        var cmd = new MySqlCommand(sql, c, tx);
        foreach (var (name, value) in p ?? []) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    /// <summary>Opens a pooled connection, retries transient failures, and tracks online state.</summary>
    private async Task<T> RunAsync<T>(Func<MySqlConnection, Task<T>> work, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var c = new MySqlConnection(connectionString);
                await c.OpenAsync(ct);
                // Column defaults (created_at, alert timestamp) must also be UTC (spec §12).
                await Cmd(c, "SET time_zone = '+00:00'").ExecuteNonQueryAsync(ct);
                var result = await work(c);
                IsOnline = true;
                LastError = null;
                return result;
            }
            catch (MySqlException ex) when (ex.IsTransient && attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay * attempt, ct);
            }
            catch (MySqlException ex) when (IsConnectionFailure(ex))
            {
                IsOnline = false;
                LastError = ex.Message;
                throw;
            }
        }
    }

    private static bool IsConnectionFailure(MySqlException ex) =>
        ex.ErrorCode is MySqlErrorCode.UnableToConnectToHost or MySqlErrorCode.AccessDenied or MySqlErrorCode.UnknownDatabase || ex.IsTransient;
}
