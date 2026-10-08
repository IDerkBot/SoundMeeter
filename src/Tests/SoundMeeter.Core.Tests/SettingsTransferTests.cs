using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Text.Json;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Импорт и экспорт всех настроек (SM-C09).
///
/// Настоящий <see cref="WasapiAudioEngine"/> и каталог во временной папке: экспорт
/// теряет поля именно потому, что снимок движка их не знает, и заглушка движка с
/// пустым <c>CreateSnapshot()</c> этот дефект проверила бы, а не поймала.
/// Аудиопоток не запускается — работают только списки стрипов.
/// </summary>
public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "SoundMeeterTransferTests_" + Guid.NewGuid().ToString("N"));

    private WasapiAudioEngine? _engine;

    public void Dispose()
    {
        _engine?.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка теста не должна ронять прогон из-за антивируса.
        }
    }

    /// <summary>Движок, пр��сет и хранилище, указывающие во временный каталог.</summary>
    private (SettingsService Settings, SettingsTransferService Transfer) NewStore()
    {
        _engine = new WasapiAudioEngine();
        var settings = new SettingsService(_engine, _root);
        settings.Settings = new AppSettings();
        return (settings, new SettingsTransferService(settings));
    }

    private static AppSettings Preset() => new()
    {
        SchemaVersion = SettingsMigrator.CurrentSchemaVersion,
        EngineWasRunning = true,
        Language = "ru",
        LogLevel = "Warning",
        Inputs =
        {
            new InputChannelModel { Id = "i1", Name = "Mic", DeviceId = "mic", VolumeDb = -3.5f },
        },
        Outputs =
        {
            new OutputBusModel { Id = "b1", Name = "Speakers", DeviceId = "dev1" },
            new OutputBusModel { Id = "b2", Name = "CABLE", DeviceId = "dev2" },
        },
    };

    private string TempFile(string name)
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, name);
    }

    /// <summary>
    /// Регрессия на главный дефект экспорта: снимок движка не содержит ни
    /// маршрутов приложений, ни дока, ни языка, ни поведения приложения. Экспорт
    /// только <c>CreateSnapshot()</c> молча терял бы восемь полей из тринадцати,
    /// и пользователь увозил бы на другой компьютер неполный микшер.
    /// </summary>
    [Fact]
    public void ExportKeepsFieldsTheEngineSnapshotDoesNotKnow()
    {
        var (settings, transfer) = NewStore();
        var live = settings.Settings;

        live.PersistentRoutes.Add(new DeviceRouteRule
        {
            ExecutablePath = @"C:\Games\game.exe", DeviceId = "dev1", AppName = "Game",
        });
        live.HiddenDeviceIds.Add("dev3");
        live.TrayEnabled = false;
        live.RunAtStartup = true;
        live.ObsDock.Enabled = true;
        live.ObsDock.Port = 17955;
        live.ObsDock.Channels.Add(new ObsDockChannelRef { StripId = "b1", Kind = ObsDockChannels.Output });

        _engine!.ApplyPreset(Preset());

        var exported = transfer.Export(TempFile("export.json"));
        Assert.True(exported.Success, exported.Message);

        var read = transfer.Read(TempFile("export.json"));
        Assert.True(read.Success, read.Message);
        var loaded = read.Settings!;

        Assert.Equal(@"C:\Games\game.exe", Assert.Single(loaded.PersistentRoutes).ExecutablePath);
        Assert.Equal("dev1", loaded.PersistentRoutes[0].DeviceId);
        Assert.Equal("Game", loaded.PersistentRoutes[0].AppName);
        Assert.Equal("dev3", Assert.Single(loaded.HiddenDeviceIds));
        Assert.False(loaded.TrayEnabled);
        Assert.True(loaded.RunAtStartup);
        Assert.True(loaded.ObsDock.Enabled);
        Assert.Equal(17955, loaded.ObsDock.Port);
        Assert.Equal("b1", Assert.Single(loaded.ObsDock.Channels).StripId);

        // И сам микшер, разумеется, тоже обязан уехать.
        Assert.Equal("i1", Assert.Single(loaded.Inputs).Id);
        Assert.Equal(2, loaded.Outputs.Count);
        Assert.Equal(-3.5f, loaded.Inputs[0].VolumeDb);
    }

    /// <summary>Экспорт и импорт — обратные операции над одним и тем же документом.</summary>
    [Fact]
    public void ExportThenReadReturnsTheSameStrips()
    {
        var (settings, transfer) = NewStore();
        settings.Settings.HiddenDeviceIds.Add("dev9");
        _engine!.ApplyPreset(Preset());

        Assert.True(transfer.Export(TempFile("roundtrip.json")).Success);

        var read = transfer.Read(TempFile("roundtrip.json"));
        Assert.True(read.Success, read.Message);

        // Применяем в тот же движок: экспорт должен был вернуть ровно то, что в нём было.
        _engine.ApplyPreset(read.Settings!);

        Assert.Equal("i1", Assert.Single(_engine.Inputs).Id);
        Assert.Equal(new[] { "b1", "b2" }, _engine.Buses.Select(b => b.Id));

        // Состояние движка тоже переносится — но по факту, а не по Preset():
        // аудиопоток в тесте не запускается, и экспорт обязан сказать правду.
        Assert.Equal(_engine.IsRunning, read.Settings!.EngineWasRunning);
        Assert.Equal("dev9", Assert.Single(settings.Settings.HiddenDeviceIds));
    }

    /// <summary>
    /// Список правил приложений копируется, а не берётся ссылкой: дальше его правит
    /// AudioService на своих потоках, и правка разобранного файла не должна была бы
    /// просочиться в живые настройки.
    /// </summary>
    [Fact]
    public void AdoptAppLevelFieldsCopiesInsteadOfSharing()
    {
        var (settings, _) = NewStore();
        var imported = Preset();
        imported.PersistentRoutes.Add(new DeviceRouteRule { ExecutablePath = "a.exe", DeviceId = "dev1" });
        imported.HiddenDeviceIds.Add("dev3");

        settings.AdoptAppLevelFields(imported);

        // Правка разобранного файла не должна просачиваться в живые настройки.
        imported.PersistentRoutes.Clear();
        imported.HiddenDeviceIds.Clear();

        Assert.Single(settings.Settings.PersistentRoutes);
        Assert.Equal("dev3", Assert.Single(settings.Settings.HiddenDeviceIds));

        // И наоборот: второе принятие заменяет список, а не дополняет общий.
        var second = Preset();
        second.PersistentRoutes.Add(new DeviceRouteRule { ExecutablePath = "b.exe" });
        settings.AdoptAppLevelFields(second);

        Assert.Equal("b.exe", Assert.Single(settings.Settings.PersistentRoutes).ExecutablePath);
        Assert.Empty(settings.Settings.HiddenDeviceIds);
    }

    [Fact]
    public void ReadRefusesAFileWrittenByANewerSchema()
    {
        var (_, transfer) = NewStore();
        var preset = Preset();
        preset.SchemaVersion = SettingsMigrator.CurrentSchemaVersion + 1;
        File.WriteAllText(TempFile("future.json"), JsonSerializer.Serialize(preset));

        var read = transfer.Read(TempFile("future.json"));

        Assert.False(read.Success);
        Assert.Null(read.Settings);
    }

    [Fact]
    public void ReadRefusesGarbageInsteadOfThrowing()
    {
        var (_, transfer) = NewStore();
        File.WriteAllText(TempFile("garbage.json"), "{ this is not json");

        var read = transfer.Read(TempFile("garbage.json"));

        Assert.False(read.Success);
        Assert.Null(read.Settings);
    }

    /// <summary>
    /// Чужой JSON разбирается в AppSettings без единой ошибки и молча даёт пустой
    /// документ. Без этой проверки импорт такого файла заменил бы настройки
    /// микшера на пустоту — а разбирать отказ пользователю пришлось бы уже по
    /// факту. Отличаем чужой файл по составу полей, а не по числу стрипов.
    /// </summary>
    [Fact]
    public void ReadRefusesForeignJsonWithoutKnownFields()
    {
        var (_, transfer) = NewStore();
        File.WriteAllText(TempFile("foreign.json"), """{"hello":"world","count":42}""");

        var read = transfer.Read(TempFile("foreign.json"));

        Assert.False(read.Success);
        Assert.Null(read.Settings);
    }

    /// <summary>
    /// Настоящий файл настроек без единого стрипа — это экспорт свежей
    /// установки, где пользователь ещё ничего не добавил. Отвергать такой файл
    /// нельзя: с тех пор как каналы создаёт только пользователь, пустой микшер
    /// законное состояние, и его перенос между машинами — тоже.
    /// </summary>
    [Fact]
    public void ReadAcceptsAnExportWithoutASingleStrip()
    {
        var (_, transfer) = NewStore();
        var empty = new AppSettings { SchemaVersion = SettingsMigrator.CurrentSchemaVersion };
        File.WriteAllText(TempFile("empty.json"), JsonSerializer.Serialize(empty));

        var read = transfer.Read(TempFile("empty.json"));

        Assert.True(read.Success, read.Message);
        Assert.Empty(read.Settings!.Inputs);
        Assert.Empty(read.Settings.Outputs);
    }

    [Fact]
    public void ReadRefusesAMissingFile()
    {
        var (_, transfer) = NewStore();

        var read = transfer.Read(TempFile("nothing-here.json"));

        Assert.False(read.Success);
        Assert.Null(read.Settings);
    }

    /// <summary>
    /// Старый файл должен импортироваться, а не отвергаться: иначе перенос настроек
    /// ломался бы ровно на машине, которая обновлялась через одну версию.
    /// </summary>
    [Fact]
    public void ReadMigratesAnOlderSchemaBeforeHandingItOver()
    {
        var (_, transfer) = NewStore();
        var preset = Preset();
        preset.SchemaVersion = 0;
        File.WriteAllText(TempFile("old.json"), JsonSerializer.Serialize(preset));

        var read = transfer.Read(TempFile("old.json"));

        Assert.True(read.Success, read.Message);
        Assert.Equal(SettingsMigrator.CurrentSchemaVersion, read.Settings!.SchemaVersion);
        Assert.Equal("i1", Assert.Single(read.Settings.Inputs).Id);
    }

    /// <summary>Неудачное имя файла — это сообщение пользователю, а не падение.</summary>
    [Fact]
    public void ExportReportsFailureInsteadOfThrowing()
    {
        var (_, transfer) = NewStore();
        _engine!.ApplyPreset(Preset());

        var exported = transfer.Export(TempFile("bad\0name.json"));

        Assert.False(exported.Success);
        Assert.NotEqual(string.Empty, exported.Message);
    }

    /// <summary>
    /// Копия предыдущих настроек — обязательная часть импорта: он заменяет всё
    /// разом, а откатить замену без копии нечем.
    /// </summary>
    [Fact]
    public void BackupCurrentWritesTheSnapshotNextToSettings()
    {
        var (settings, _) = NewStore();
        settings.Settings.TrayEnabled = false;
        settings.Settings.HiddenDeviceIds.Add("dev7");
        _engine!.ApplyPreset(Preset());

        var backup = settings.BackupCurrent();

        Assert.NotNull(backup);
        Assert.Equal(settings.SettingsPath + ".bak", backup);
        Assert.True(File.Exists(backup));

        var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(backup!))!;
        Assert.False(restored.TrayEnabled);
        Assert.Equal("dev7", Assert.Single(restored.HiddenDeviceIds));
        Assert.Equal("i1", Assert.Single(restored.Inputs).Id);
    }
}