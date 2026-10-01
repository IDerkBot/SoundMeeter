using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Tests.Infrastructure;
using System.Text.Json;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Миграции схемы настроек. Файл settings.json принадлежит пользователю и
/// переживает версии приложения, поэтому миграция — единственное место, где
/// решается, что делать с мусором в чужом JSON: тут это и проверяется.
/// </summary>
public class SettingsMigratorTests
{
    [Fact]
    public void VersionZeroGetsFuncSlotsAndTheCurrentSchema()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 0,
            Inputs = new List<InputChannelModel> { new() { Id = "i1", Name = "Mic" } }
        };

        SettingsMigrator.Migrate(settings, out _);

        Assert.Equal(SettingsMigrator.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(2, settings.Inputs[0].FuncButtons.Count);
        Assert.Equal(InputChannelModel.NoFuncEngaged, settings.Inputs[0].EngagedFunc);
    }

    [Fact]
    public void VersionTwoRepairsNullsAndOutOfRangeEngagement()
    {
        var settings = MessyVersionTwo();
        var inputs = settings.Inputs;

        SettingsMigrator.Migrate(settings, out _);

        Assert.Equal(SettingsMigrator.CurrentSchemaVersion, settings.SchemaVersion);

        // null вместо списка: настройку нельзя бросать, стрип должен выжить.
        Assert.Equal(2, inputs[0].FuncButtons.Count);
        Assert.Empty(inputs[0].FuncBaseRouting);

        // Номер кнопки вне диапазона — сбрасываем, иначе стрип «зависнет» с
        // несуществующей нажатой кнопкой.
        Assert.Equal(InputChannelModel.NoFuncEngaged, inputs[0].EngagedFunc);

        // Нажата кнопка без назначения — снимаем: нажатие восстановить нечем.
        Assert.Equal(InputChannelModel.NoFuncEngaged, inputs[1].EngagedFunc);

        // Корректное состояние не трогаем.
        Assert.Equal(0, inputs[2].EngagedFunc);

        // Нажатой кнопке без базового роутинга возвращать нечего.
        Assert.Equal(InputChannelModel.NoFuncEngaged, inputs[3].EngagedFunc);
    }

    [Fact]
    public void VersionTwoCleansUpAssignmentsAndBaseRouting()
    {
        var settings = MessyVersionTwo();

        SettingsMigrator.Migrate(settings, out _);

        var strip = settings.Inputs[1];
        var func = strip.FuncButtons[0];

        // Дубликаты, пробелы и Id несуществующей шины выкидываем, метку и режим
        // оставляем: это правка пользователя, а не мусор.
        Assert.Equal(new[] { "v1", "b1" }, func.BusIds);
        Assert.Equal("STREAM", func.Label);
        Assert.False(func.Exclusive);

        Assert.Single(strip.FuncBaseRouting);
        Assert.False(strip.FuncBaseRouting["b1"]);
    }

    [Fact]
    public void JsonRoundTripKeepsEngagementAndBase()
    {
        var settings = MessyVersionTwo();
        SettingsMigrator.Migrate(settings, out _);

        var json = JsonSerializer.Serialize(settings);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        SettingsMigrator.Migrate(reloaded, out _);

        Assert.Equal(InputChannelModel.NoFuncEngaged, reloaded.Inputs[1].EngagedFunc);
        Assert.Equal(0, reloaded.Inputs[2].EngagedFunc);
        Assert.True(reloaded.Inputs[2].FuncBaseRouting["b1"]);
        Assert.Equal(new[] { "v1", "b1" }, reloaded.Inputs[1].FuncButtons[0].BusIds);
    }

    [Fact]
    public void NewerSchemaIsRejectedInsteadOfGuessed()
    {
        var settings = new AppSettings { SchemaVersion = 99 };

        var outcome = SettingsMigrator.Migrate(settings, out _);

        Assert.Equal(MigrationOutcome.UnsupportedNewerVersion, outcome);
    }

    /// <summary>Настройки, как их может оставить реальный settings.json.</summary>
    private static AppSettings MessyVersionTwo() => new()
    {
        SchemaVersion = 2,
        Inputs = new List<InputChannelModel>
        {
            new() { Id = "i1", FuncButtons = null!, EngagedFunc = 5, FuncBaseRouting = null! },
            new()
            {
                Id = "i2",
                EngagedFunc = 1,
                FuncBaseRouting = new Dictionary<string, bool> { ["b1"] = true, [" "] = false, ["b1"] = false },
                FuncButtons = new List<FuncButtonModel>
                {
                    new() { Label = "STREAM", Exclusive = false, BusIds = new List<string> { "v1", "v1", " ", "b1" } },
                    new(),
                    new()
                }
            },
            new()
            {
                Id = "i3",
                EngagedFunc = 0,
                FuncBaseRouting = new Dictionary<string, bool> { ["b1"] = true },
                FuncButtons = new List<FuncButtonModel> { new() { BusIds = new List<string> { "b1" } } }
            },
            new()
            {
                Id = "i4",
                EngagedFunc = 0,
                FuncBaseRouting = new Dictionary<string, bool>(),
                FuncButtons = new List<FuncButtonModel> { new() { BusIds = new List<string> { "b1" } } }
            }
        }
    };
}
