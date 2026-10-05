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

    /// <summary>
    /// Первый запуск: файла настроек нет, и микшер должен остаться пустым.
    /// Раньше движок на старте создавал два пустых входа и две пустые шины, а
    /// осмотр каталога — ещё по стрипу на каждое устройство, то есть набор
    /// каналов определяла машина. Теперь стрипы появляются только по команде
    /// пользователя, и снимок пустого микшера обязан оставаться пустым —
    /// иначе «пустой» старт тихо расползается по кругу.
    /// </summary>
    [Fact]
    public void AnEmptyPresetLeavesTheMixerEmpty()
    {
        using var engine = new WasapiAudioEngine();
        var empty = new AppSettings { SchemaVersion = SettingsMigrator.CurrentSchemaVersion };
        SettingsMigrator.Migrate(empty, out _);

        engine.ApplyPreset(empty);

        Assert.Empty(engine.Inputs);
        Assert.Empty(engine.Buses);

        // Ни добавления, ни повторного «перезапуска» не должно ронять стрипы:
        // снимок пустого микшера и есть законное состояние.
        engine.ApplyPreset(engine.CreateSnapshot());
        Assert.Empty(engine.Inputs);
        Assert.Empty(engine.Buses);
    }

    /// <summary>Каналы добавляет пользователь, а не движок: одна команда — один стрип.</summary>
    [Fact]
    public void AddInputAndAddBusCreateExactlyOneStrip()
    {
        using var engine = new WasapiAudioEngine();

        engine.AddInput();
        engine.AddBus();

        Assert.Single(engine.Inputs);
        Assert.Single(engine.Buses);
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
