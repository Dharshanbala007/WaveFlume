using FluentAssertions;
using WaveFlume.App.Services;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.App.Tests;

public class ServicesTests
{
    [Fact]
    public void Password_hash_verifies_and_rejects()
    {
        var hash = Secrets.HashPassword("flume-admin");
        hash.Should().StartWith("pbkdf2-sha256$").And.NotContain("flume-admin");
        Secrets.VerifyPassword("flume-admin", hash).Should().BeTrue();
        Secrets.VerifyPassword("flume-Admin", hash).Should().BeFalse();
        Secrets.VerifyPassword("x", null).Should().BeFalse();
        Secrets.HashPassword("same").Should().NotBe(Secrets.HashPassword("same"), "salted");
    }

    [Fact]
    public void Dpapi_round_trip()
    {
        var p = Secrets.Protect("s3cret");
        p.Should().NotBeNull().And.NotContain("s3cret");
        Secrets.Unprotect(p).Should().Be("s3cret");
        Secrets.Unprotect("not-base64-garbage==").Should().BeNull();
        Secrets.Protect("").Should().BeNull();
    }

    [Fact]
    public void Settings_store_seeds_profiles_and_refuses_raised_ceilings()
    {
        using var env = new TestEnv();
        env.App.Store.ProfileFiles().Should().Contain(["modbus_generic.json", "omron_fins_dm.json", "opcua_generic.json"]);
        var bad = env.App.Settings with { Flume = env.App.Settings.Flume with { Limits = env.App.Settings.Flume.Limits with { MaxStrokeM = 0.46 } } };
        FluentActions.Invoking(() => env.App.Save(bad)).Should().Throw<InvalidOperationException>();
        new AppState(env.App.Store).Settings.Flume.Limits.MaxStrokeM.Should().Be(0.35, "nothing was written");
    }

    [Fact]
    public void Tagmap_errors_block_connect_for_missing_or_unsupported_profiles()
    {
        using var env = new TestEnv();
        env.App.TagMapErrors.Should().BeEmpty("simulator needs no TagMap");
        env.App.Save(env.App.Settings with { Connection = new ConnectionSettings { Protocol = PlcProtocolType.OmronFins } });
        env.App.TagMapErrors.Should().BeEmpty();
        env.App.Save(env.App.Settings with { Connection = new ConnectionSettings { Protocol = PlcProtocolType.ModbusTcp, TagMapProfile = "nope.json" } });
        env.App.TagMapErrors.Should().ContainSingle().Which.Should().Contain("not found");
        env.App.Save(env.App.Settings with { Connection = new ConnectionSettings { Protocol = PlcProtocolType.Ads, TagMapProfile = "omron_fins_dm.json" } });
        env.App.TagMapErrors.Should().ContainSingle().Which.Should().Contain("release 3");
    }

    [Fact]
    public void Recipe_store_crud()
    {
        using var env = new TestEnv();
        var r = new WaveRecipe { TestNumber = "T-0001", Request = new WaveRequest() };
        env.Recipes.Insert(r);
        FluentActions.Invoking(() => env.Recipes.Insert(r)).Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
        env.Recipes.NextTestNumber().Should().Be("T-0002");
        env.Recipes.Update(r with { Request = new WaveRequest { PeriodS = 2 } });
        env.Recipes.Get("t-0001")!.Request.PeriodS.Should().Be(2);
        FluentActions.Invoking(() => env.Recipes.Update(r with { TestNumber = "X" })).Should().Throw<InvalidOperationException>();
        env.Recipes.Delete("T-0001");
        env.Recipes.List().Should().BeEmpty();
    }

    [Fact]
    public void Alarm_banner_shows_highest_unacknowledged()
    {
        using var env = new TestEnv();
        var a = env.Alarms;
        a.Raise(AlertSeverity.Warning, AlertSources.Comms, "w");
        a.Raise(AlertSeverity.Critical, AlertSources.Comms, "c");
        a.Raise(AlertSeverity.Warning, AlertSources.Comms, "w2");
        a.Banner!.Description.Should().Be("c");
        a.ActiveCount.Should().Be(3);
        a.Acknowledge(a.Banner);
        a.Banner!.Description.Should().Be("w2", "newest of the remaining priority");
        a.AcknowledgeAll();
        a.Banner.Should().BeNull();
        a.Log.Should().HaveCount(3).And.OnlyContain(e => e.Acknowledged);
    }

    [Fact]
    public void Calibration_wizard_fits_and_saves_only_for_admin()
    {
        using var env = new TestEnv();
        var vm = new WaveFlume.App.ViewModels.CalibrationViewModel(env.App, new PlcSession(env.Alarms, env.App), env.Dialogs);
        vm.Rows.Should().HaveCount(3).And.OnlyContain(r => r.RSquared == "nominal");
        vm.CaptureCommand.Execute(null);
        env.Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("Connect");

        vm.Channel = 1;
        foreach (var (ma, m) in new[] { (4.0, -0.2), (12.0, 0.0), (20.0, 0.2) }) vm.Points.Add(new(ma, m));
        vm.FitCommand.Execute(null);
        vm.FitText.Should().Contain("R² = 1.00000");
        vm.SaveCommand.CanExecute(null).Should().BeFalse("operator mode");
        env.App.IsAdmin = true;
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
        vm.SaveCommand.Execute(null);
        env.App.Calibration.For(1, env.App.Settings.Flume.Probes).Gain.Should().BeApproximately(0.025, 1e-12);
        vm.Rows[1].RSquared.Should().Be("1.00000");
        new AppState(env.App.Store).Calibration.Channels.Should().ContainSingle("persisted to calibration.json");
    }

    [Fact]
    public void Alarm_raised_event_feeds_the_database_writer()
    {
        using var env = new TestEnv();
        var seen = new List<Alert>();
        env.Alarms.Raised += seen.Add;
        env.Alarms.Raise(AlertSeverity.Critical, AlertSources.WaveProbe, "x");
        seen.Should().ContainSingle();
    }

    [Theory]
    [InlineData(StatusBits.None, RunState.Connected)]
    [InlineData(StatusBits.Enabled, RunState.ServosEnabled)]
    [InlineData(StatusBits.Enabled | StatusBits.Homed, RunState.Homed)]
    [InlineData(StatusBits.Enabled | StatusBits.Homed | StatusBits.TrajectoryLoaded, RunState.TrajectoryLoaded)]
    [InlineData(StatusBits.Enabled | StatusBits.Homed | StatusBits.TrajectoryLoaded | StatusBits.Running, RunState.Running)]
    [InlineData(StatusBits.Running | StatusBits.SmoothStopping, RunState.Stopping)]
    [InlineData(StatusBits.Running | StatusBits.Fault, RunState.Faulted)]
    [InlineData(StatusBits.EStopActive, RunState.Faulted)]
    public void Status_word_maps_to_run_state(StatusBits bits, RunState expected) =>
        StatusMapping.ToRunState(true, bits).Should().Be(expected);

    [Fact]
    public void Run_state_rules_gate_buttons()
    {
        StatusMapping.ToRunState(false, StatusBits.Running).Should().Be(RunState.Disconnected);
        RunStateRules.IsAllowed(RunState.Homed, OperatorAction.Start).Should().BeFalse("send first");
        RunStateRules.IsAllowed(RunState.TrajectoryLoaded, OperatorAction.Start).Should().BeTrue();
        RunStateRules.IsAllowed(RunState.Running, OperatorAction.Send).Should().BeFalse();
        RunStateRules.IsAllowed(RunState.Running, OperatorAction.Disconnect).Should().BeFalse();
        RunStateRules.IsAllowed(RunState.Running, OperatorAction.EStop).Should().BeTrue();
        RunStateRules.IsAllowed(RunState.Faulted, OperatorAction.Reset).Should().BeTrue();
        RunStateRules.IsAllowed(RunState.Faulted, OperatorAction.Start).Should().BeFalse();
    }
}
