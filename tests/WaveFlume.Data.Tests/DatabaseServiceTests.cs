using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Data;

namespace WaveFlume.Data.Tests;

/// <summary>
/// Live MySQL tests. They run only when a test database is configured, via the WAVEFLUME_TEST_MYSQL
/// environment variable or %LOCALAPPDATA%\WaveFlume\test-mysql.txt (a connection string; never logged).
/// They drop and recreate the four tables in that database.
/// </summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable("WAVEFLUME_TEST_MYSQL") is { Length: > 0 } env ? env
        : File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : null;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveFlume", "test-mysql.txt");

    public MySqlFactAttribute()
    {
        if (ConnectionString is null) Skip = "No test MySQL configured (WAVEFLUME_TEST_MYSQL or test-mysql.txt).";
    }
}

[Collection("mysql")] // tests share one database: run them sequentially
public class DatabaseServiceTests
{
    private static readonly string Schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema.sql"));

    private static async Task<DatabaseService> FreshDb()
    {
        var db = new DatabaseService(MySqlFactAttribute.ConnectionString!);
        await using (var c = new MySqlConnector.MySqlConnection(MySqlFactAttribute.ConnectionString))
        {
            await c.OpenAsync();
            foreach (var t in new[] { "telemetry_alerts", "test_runs", "wave_recipes", "system_settings" })
                await new MySqlConnector.MySqlCommand($"DROP TABLE IF EXISTS {t}", c).ExecuteNonQueryAsync();
        }
        await db.InitializeSchemaAsync(Schema, default);
        await db.InitializeSchemaAsync(Schema, default); // idempotent
        return db;
    }

    private static WaveRecipe Recipe(string tn, double period = 1.0) => new()
    {
        TestNumber = tn,
        Request = new WaveRequest { Type = WaveType.Bichromatic, PeriodS = period, Height2M = 0.02, Period2S = 1.3, FocusXM = 7, UserFilePath = @"C:\x.csv" },
        CalcStrokeM = 0.0136, CalcForceKn = 0.177,
    };

    [MySqlFact]
    public async Task Connection_and_schema()
    {
        var db = await FreshDb();
        var (ok, msg) = await db.TestAsync(default);
        ok.Should().BeTrue(msg);
        db.IsOnline.Should().BeTrue();
    }

    [MySqlFact]
    public async Task Recipe_crud_round_trips_every_field()
    {
        var db = await FreshDb();
        int id = await db.InsertRecipeAsync(Recipe("T-0001"), default);
        id.Should().BePositive();
        await db.Invoking(d => d.InsertRecipeAsync(Recipe("T-0001"), default)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");

        await db.UpdateRecipeAsync(Recipe("T-0001", 2.5), default);
        var back = (await db.ListRecipesAsync(default)).Single();
        back.Request.Should().BeEquivalentTo(Recipe("T-0001", 2.5).Request);
        back.CalcStrokeM.Should().BeApproximately(0.0136, 1e-6);
        back.CreatedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2), "created_at default is UTC");

        await db.Invoking(d => d.UpdateRecipeAsync(Recipe("NOPE"), default)).Should().ThrowAsync<InvalidOperationException>();
        (await db.UpsertRecipeAsync(Recipe("T-0001", 3), default)).Should().Be(id);
        await db.DeleteRecipeAsync("T-0001", default);
        (await db.ListRecipesAsync(default)).Should().BeEmpty();
    }

    [MySqlFact]
    public async Task Run_lifecycle_alerts_and_restrict()
    {
        var db = await FreshDb();
        var start = new DateTime(2026, 10, 30, 9, 0, 0, 123, DateTimeKind.Utc);
        int run = await db.StartRunAsync(Recipe("T-0007"), new TestRunRecord
        {
            TestNumber = "T-0007", StartUtc = start, ExtraRecordS = 1.5, CsvPath = @"C:\runs\a.csv", LimitsSnapshotJson = "{\"MaxStrokeM\":0.35}",
        }, default);
        await db.InsertAlertAsync(new Alert(start.AddSeconds(3), AlertSeverity.EStop, AlertSources.PlcStatus, "E-stop pressed"), run, default);
        await db.InsertAlertAsync(new Alert(start.AddSeconds(4), AlertSeverity.Warning, AlertSources.Comms, new string('x', 600)), null, default);
        await db.FinishRunAsync(run, RunStatus.EStopped, start.AddSeconds(10), new RunStatistics(0.05, 0.06, 0.07, 1.0, 1.1, 1.2), default);
        await db.SetRunPdfAsync(run, @"C:\runs\a.pdf", default);

        var r = (await db.ListRunsAsync(default)).Single();
        r.TestNumber.Should().Be("T-0007");
        r.Status.Should().Be(RunStatus.EStopped);
        r.StartUtc.Should().Be(start, "DATETIME(3) keeps milliseconds");
        r.Statistics!.H13M.Should().BeApproximately(0.05, 1e-6);
        r.PdfPath.Should().EndWith("a.pdf");

        var alerts = await db.AlertsForRunAsync(run, default);
        alerts.Should().ContainSingle().Which.Severity.Should().Be(AlertSeverity.EStop);

        await db.Invoking(d => d.DeleteRecipeAsync("T-0007", default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*runs*", "ON DELETE RESTRICT keeps run history");
    }

    [MySqlFact]
    public async Task System_settings_single_row_upsert()
    {
        var db = await FreshDb();
        (await db.LoadSystemSettingsAsync(default)).Should().BeNull();
        var row = new SystemSettingsRow(PaddleType.Flap, 0.1, 350, 1570, null, 0.5, 6, 0.8, 0.6, 6, 125, 125,
            "OmronFins", "192.168.250.1", 9600, "{\"FinsDestNode\":\"1\"}", "omron_fins_dm.json", "192.168.169.5", 1.0);
        await db.SaveSystemSettingsAsync(row, default);
        await db.SaveSystemSettingsAsync(row with { MaxAccelMms2 = 5000 }, default);
        var back = await db.LoadSystemSettingsAsync(default);
        back.Should().BeEquivalentTo(row with { MaxAccelMms2 = 5000 });
    }

    [Fact]
    public async Task Offline_database_is_reported_not_thrown_by_test()
    {
        var db = new DatabaseService(DatabaseService.BuildConnectionString("127.0.0.1", 1, "x", "y", "z"));
        var (ok, msg) = await db.TestAsync(default);
        ok.Should().BeFalse();
        msg.Should().NotBeNullOrEmpty();
        db.IsOnline.Should().BeFalse();
    }
}
