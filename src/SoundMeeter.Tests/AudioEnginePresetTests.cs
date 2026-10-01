using SoundMeeter.Models;
using SoundMeeter.Services;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Применение пресета к настоящему движку: мёртвые Id шин и глубокое копирование
/// снимка. Здесь нужен реальный <see cref="WasapiAudioEngine"/>, а не заглушка:
/// чистка Id и копирование живут внутри него, и заглушка их бы не проверила.
/// </summary>
/// <remarks>
/// Аудиопоток не запускается — пресет только применяется к спискам стрипов, так
/// что тест не требует ни устройств, ни прав администратора.
/// </remarks>
public class AudioEnginePresetTests
{
    private static AppSettings PresetWithDeadBus() => new()
    {
        SchemaVersion = SettingsMigrator.CurrentSchemaVersion,
        Inputs = new List<InputChannelModel>
        {
            new()
            {
                Id = "i1",
                Name = "Mic",
                DeviceId = "mic",
                EngagedFunc = 0,
                FuncBaseRouting = new Dictionary<string, bool> { ["b1"] = true, ["b2"] = false, ["ghost"] = true },
                FuncButtons = new List<FuncButtonModel>
                {
                    new() { Label = "СТРИМ", Exclusive = true, BusIds = new List<string> { "b1", "ghost" } },
                    new() { Label = "Ми", Exclusive = false, BusIds = new List<string> { "b2" } }
                }
            }
        },
        Outputs = new List<OutputBusModel>
        {
            new() { Id = "b1", Name = "Speakers", DeviceId = "dev1" },
            new() { Id = "b2", Name = "CABLE-A Output", DeviceId = "dev2" }
        }
    };

    [Fact]
    public void ApplyPresetKeepsEngagementAndPrunesDeadBusIds()
    {
        using var engine = new WasapiAudioEngine();
        var preset = PresetWithDeadBus();
        SettingsMigrator.Migrate(preset, out _);

        engine.ApplyPreset(preset);

        var strip = engine.Inputs[0];
        Assert.Equal(0, strip.EngagedFunc);
        Assert.Equal(new[] { "b1" }, strip.FuncButtons[0].BusIds);
        Assert.Equal(2, strip.FuncBaseRouting.Count);
        Assert.True(strip.FuncBaseRouting["b1"]);
    }

    [Fact]
    public void SnapshotIsADeepCopy()
    {
        using var engine = new WasapiAudioEngine();
        var preset = PresetWithDeadBus();
        SettingsMigrator.Migrate(preset, out _);
        engine.ApplyPreset(preset);

        var snapshot = engine.CreateSnapshot();
        Assert.Equal(0, snapshot.Inputs[0].EngagedFunc);
        Assert.True(snapshot.Inputs[0].FuncBaseRouting["b1"]);

        snapshot.Inputs[0].FuncBaseRouting["b1"] = false;
        snapshot.Inputs[0].FuncButtons[0].BusIds.Add("b2");

        // Правка снимка не должна просачиваться в живой движок: снимок идёт в
        // файл настроек, а движок продолжает играть.
        Assert.True(engine.Inputs[0].FuncBaseRouting["b1"]);
        Assert.DoesNotContain("b2", engine.Inputs[0].FuncButtons[0].BusIds);
    }

    [Fact]
    public void ReapplyingASnapshotRestoresTheEngagedButton()
    {
        using var first = new WasapiAudioEngine();
        var preset = PresetWithDeadBus();
        SettingsMigrator.Migrate(preset, out _);
        first.ApplyPreset(preset);

        using var second = new WasapiAudioEngine();
        second.ApplyPreset(first.CreateSnapshot());

        var strip = new ViewModels.InputChannelViewModel(
            second.Inputs[0], second, second.Buses, second.Catalog, () => { });

        Assert.True(strip.Func1.IsEngaged);
        Assert.False(strip.Func2.IsEngaged);

        strip.Func1.IsEngaged = false;
        Assert.DoesNotContain("ghost", second.Inputs[0].FuncButtons[0].BusIds);
    }
}
