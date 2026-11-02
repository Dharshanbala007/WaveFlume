using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WaveFlume.Core;
using WaveFlume.Data;

namespace WaveFlume.App.Services;

/// <summary>
/// The app's view of MySQL: builds the <see cref="DatabaseService"/> from local settings (DPAPI password),
/// tracks online/offline for the status bar, and writes alerts from a background queue so the UI never
/// waits on the database. When disabled or offline, recipes fall back to local JSON and runs to CSV only.
/// </summary>
public sealed partial class DataSession : ObservableObject
{
    public const int AlertQueueCapacity = 1000;

    private readonly ILogger<DataSession> _log;
    private readonly Channel<(Alert Alert, int? RunId)> _alerts =
        Channel.CreateBounded<(Alert, int?)>(new BoundedChannelOptions(AlertQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private DatabaseSettings _settings = new();

    public DataSession(AppState app, ILogger<DataSession> log)
    {
        _log = log;
        Configure(app.Settings.Database);
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.Settings) && app.Settings.Database != _settings) Configure(app.Settings.Database);
        };
        _ = Task.Run(AlertLoopAsync);
    }

    public DatabaseService? Db { get; private set; }

    /// <summary>Run that alerts are attached to (telemetry_alerts.run_id).</summary>
    public int? CurrentRunId { get; set; }

    [ObservableProperty]
    private string _statusText = "DB: disabled (local JSON)";

    public void Configure(DatabaseSettings s)
    {
        _settings = s;
        Db = s.Enabled && !string.IsNullOrWhiteSpace(s.Host)
            ? new DatabaseService(DatabaseService.BuildConnectionString(s.Host, s.Port, s.Database, s.User, Secrets.Unprotect(s.ProtectedPassword)))
            : null;
        StatusText = Db is null ? "DB: disabled (local JSON)" : "DB: connecting...";
        if (Db is not null) _ = RefreshAsync();
    }

    public async Task<(bool Ok, string Message)> RefreshAsync()
    {
        if (Db is null) return (false, "Database disabled in Settings.");
        var (ok, msg) = await Db.TestAsync(CancellationToken.None);
        StatusText = ok ? $"DB: online ({_settings.Host}/{_settings.Database})" : "DB: offline (local JSON)";
        if (!ok) _log.LogWarning("Database offline: {Message}", msg);
        return (ok, msg);
    }

    /// <summary>Runs a database operation; on a connection failure marks offline and returns null.</summary>
    public async Task<T?> TryAsync<T>(Func<DatabaseService, Task<T>> work)
    {
        if (Db is not { } db) return default;
        try
        {
            var r = await work(db);
            StatusText = $"DB: online ({_settings.Host}/{_settings.Database})";
            return r;
        }
        catch (MySqlException ex)
        {
            MarkOffline(ex);
            return default;
        }
    }

    public void MarkOffline(Exception ex)
    {
        StatusText = "DB: offline (local JSON)";
        _log.LogWarning(ex, "Database operation failed; continuing offline");
    }

    public void Enqueue(Alert alert) => _alerts.Writer.TryWrite((alert, CurrentRunId));

    private async Task AlertLoopAsync()
    {
        await foreach (var (alert, runId) in _alerts.Reader.ReadAllAsync())
        {
            if (Db is null) continue;
            await TryAsync(async db => { await db.InsertAlertAsync(alert, runId, CancellationToken.None); return 0; });
        }
    }
}

public interface IRecipeRepository
{
    Task<IReadOnlyList<WaveRecipe>> ListAsync();
    Task InsertAsync(WaveRecipe recipe);
    Task UpdateAsync(WaveRecipe recipe);
    Task DeleteAsync(string testNumber);
}

/// <summary>MySQL when available, local JSON otherwise (spec §11.2 offline fallback).</summary>
public sealed class RecipeRepository(DataSession data, RecipeStore local) : IRecipeRepository
{
    private async Task<T> Use<T>(Func<DatabaseService, Task<T>> db, Func<T> fallback)
    {
        if (data.Db is { } d)
        {
            try { return await db(d); }
            catch (MySqlException ex) { data.MarkOffline(ex); }
        }
        return fallback();
    }

    public Task<IReadOnlyList<WaveRecipe>> ListAsync() => Use(d => d.ListRecipesAsync(default), local.List);
    public Task InsertAsync(WaveRecipe r) => Use(async d => await d.InsertRecipeAsync(r, default), () => { local.Insert(r); return 0; });
    public Task UpdateAsync(WaveRecipe r) => Use(async d => { await d.UpdateRecipeAsync(r, default); return 0; }, () => { local.Update(r); return 0; });
    public Task DeleteAsync(string tn) => Use(async d => { await d.DeleteRecipeAsync(tn, default); return 0; }, () => { local.Delete(tn); return 0; });
}
