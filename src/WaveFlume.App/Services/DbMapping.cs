using System.Text.Json;
using WaveFlume.Core;
using WaveFlume.Data;

namespace WaveFlume.App.Services;

/// <summary>Local settings → system_settings row (DB units: mm, mm/s, mm/s²). The DB password is never included.</summary>
public static class DbMapping
{
    public static SystemSettingsRow ToRow(AppSettings s)
    {
        var f = s.Flume;
        var l = f.Limits;
        var c = s.Connection;
        return new SystemSettingsRow(
            f.PaddleType, f.HingeHeightM, Units.MToMm(l.MaxStrokeM), Units.MToMm(l.MaxVelocityMs),
            l.MaxAccelMs2 is { } a ? Units.MToMm(a) : null, f.PaddleWidthM, f.NumPaddles, f.MaxWaterDepthM, l.TMinS, l.TMaxS,
            l.CommandFsHz, l.TelemetryRateHz, c.Protocol.ToString(), c.IpAddress, c.ToEndpoint().Port,
            JsonSerializer.Serialize(c.Extras), c.TagMapProfile, c.ExternalDaqIp, s.CalibrationFactor);
    }
}
