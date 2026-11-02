using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaveFlume.App.Services;
using WaveFlume.Core;

namespace WaveFlume.App.ViewModels;

/// <summary>Window shell: alarm banner, status bar, clock, and the UI tick that drives the tabs.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppState _app;

    public MainViewModel(AppState app, AlarmService alarms, PlcSession session, DataSession data,
        WaveGenerationViewModel waveGeneration, WavePaddleViewModel wavePaddle, SettingsViewModel settings, CalibrationViewModel calibration)
    {
        _app = app;
        (Alarms, Session, Data, WaveGeneration, WavePaddle, Settings, Calibration) =
            (alarms, session, data, waveGeneration, wavePaddle, settings, calibration);
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.Settings)) OnPropertyChanged(nameof(AccelText));
            if (e.PropertyName is nameof(AppState.IsAdmin)) OnPropertyChanged(nameof(ModeText));
        };
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlcSession.State)) OnPropertyChanged(nameof(RunStateText));
        };
    }

    public AlarmService Alarms { get; }
    public PlcSession Session { get; }
    public WaveGenerationViewModel WaveGeneration { get; }
    public WavePaddleViewModel WavePaddle { get; }
    public SettingsViewModel Settings { get; }
    public CalibrationViewModel Calibration { get; }
    public DataSession Data { get; }

    [ObservableProperty] private string _clock = "";

    public string RunStateText => $"Run: {Session.State}";
    public string ModeText => _app.IsAdmin ? "Mode: Admin" : "Mode: Operator";

    /// <summary>Spec §2: shown permanently while a_max is not configured.</summary>
    public string? AccelText => _app.Settings.Flume.Limits.MaxAccelMs2 is null ? "Acceleration limit not configured" : null;

    public bool IsRunning => Session.State is RunState.Running or RunState.Stopping;

    [RelayCommand]
    private void Acknowledge() => Alarms.Acknowledge(Alarms.Banner);

    [RelayCommand]
    private void AcknowledgeAll() => Alarms.AcknowledgeAll();

    public async Task TickAsync()
    {
        Clock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await WavePaddle.TickAsync();
    }
}
