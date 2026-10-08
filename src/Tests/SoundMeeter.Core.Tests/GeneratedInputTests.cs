using SoundMeeter.Models;
using SoundMeeter.Services;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Генерируемые входные стрипы (SM-E01) — канал, в который пишет код приложения.
///
/// Проверяется на настоящем <see cref="WasapiAudioEngine"/>: он трогает устройства
/// только когда у стрипа есть <c>DeviceId</c>, а у генерируемого его нет по
/// определению. Поэтому здесь не нужен ни звуковой сток, ни права администратора,
/// а поведение проверяется ровно то, какое увидит стример в микшере.
/// </summary>
public class GeneratedInputTests
{
    [Fact]
    public void AGeneratedStripIsCreatedWithoutADeviceAndIsImmediatelyAvailable()
    {
        using var engine = new WasapiAudioEngine();

        string id = engine.EnsureGeneratedInput("TTS");

        var strip = Assert.Single(engine.Inputs);
        Assert.Equal(id, strip.Id);
        Assert.True(strip.IsGenerated);
        Assert.True(strip.IsAvailable);
        Assert.Equal("", strip.DeviceId);
        Assert.Equal("TTS", strip.Name);
    }

    /// <summary>
    /// Повторный клик по «Применить» не должен плодить каналы: окно настроек
    /// вызывает это каждый раз, а стрип с тем же именем уже есть.
    /// </summary>
    [Fact]
    public void AskingForTheSameStripTwiceDoesNotDuplicateIt()
    {
        using var engine = new WasapiAudioEngine();

        string first = engine.EnsureGeneratedInput("TTS");
        string second = engine.EnsureGeneratedInput("tts");

        Assert.Equal(first, second);
        Assert.Single(engine.Inputs);
    }

    /// <summary>
    /// Переименованный пользователем канал не должен приводить к появлению его
    /// копии: имя для поиска берётся и из заголовка, и из подписи.
    /// </summary>
    [Fact]
    public void ARenamedStripIsFoundByItsVisibleName()
    {
        using var engine = new WasapiAudioEngine();

        string id = engine.EnsureGeneratedInput("TTS");
        engine.Inputs[0].ChannelName = "Озвучка";

        Assert.Equal(id, engine.EnsureGeneratedInput("Озвучка"));
        Assert.Single(engine.Inputs);
    }

    [Fact]
    public void DifferentNamesGetDifferentStrips()
    {
        using var engine = new WasapiAudioEngine();

        string first = engine.EnsureGeneratedInput("TTS");
        string second = engine.EnsureGeneratedInput("Анонсы");

        Assert.NotEqual(first, second);
        Assert.Equal(2, engine.Inputs.Count);
    }

    [Fact]
    public void CreatingAStripNotifiesTheInterfaceSoStripsRebuild()
    {
        using var engine = new WasapiAudioEngine();
        int notifications = 0;
        engine.ChannelsChanged += () => notifications++;

        engine.EnsureGeneratedInput("TTS");

        // Без уведомления полосы микшера остались бы со старым списком, и нового
        // канала было бы не видно до перечитывания устройств.
        Assert.Equal(1, notifications);
    }

    /// <summary>
    /// Генерируемый стрип доступен всегда: у него нет устройства, которое можно
    /// было бы отключить. Проверяется обновлением каталога — именно оно обычно и
    /// снимает отметку «доступен» у пропавших устройств.
    /// </summary>
    [Fact]
    public void AGeneratedStripStaysAvailableAfterScanningDevices()
    {
        using var engine = new WasapiAudioEngine();
        engine.EnsureGeneratedInput("TTS");

        engine.RefreshDevices();

        Assert.True(engine.Inputs[0].IsAvailable);
    }

    [Fact]
    public void AGeneratedStripSurvivesAPresetRoundTrip()
    {
        using var engine = new WasapiAudioEngine();
        string id = engine.EnsureGeneratedInput("TTS");

        var preset = engine.CreateSnapshot();
        using var restarted = new WasapiAudioEngine();
        restarted.ApplyPreset(preset);

        var strip = Assert.Single(restarted.Inputs);
        Assert.True(strip.IsGenerated);
        Assert.Equal(id, strip.Id);
    }

    #region Приём синтезированного аудио

    /// <summary>
    /// Остановленный микшер — некуда говорить. Модуль проверяет это ДО синтеза, и
    /// здесь проверяется, что движок отвечает честно.
    /// </summary>
    [Fact]
    public void AudioIsRefusedWhileTheEngineIsStopped()
    {
        using var engine = new WasapiAudioEngine();
        string id = engine.EnsureGeneratedInput("TTS");

        Assert.False(engine.IsRunning);
        Assert.False(engine.PushAudio(id, new float[960]));
    }

    /// <summary>
    /// Снятие маршрута закрывает источник, и синтезировать дальше незачем: фраза
    /// уйдёт в никуда и потратит несколько секунд процессора.
    /// </summary>
    [Fact]
    public void AudioIsRefusedWhenTheStripIsNotRoutedAnywhere()
    {
        using var engine = new WasapiAudioEngine();
        engine.AddBus();                                  // шина есть, но без устройства
        string id = engine.EnsureGeneratedInput("TTS");
        engine.Start();

        Assert.True(engine.IsRunning);
        Assert.False(engine.PushAudio(id, new float[960]));
    }

    [Fact]
    public void AudioForAnUnknownStripIsRefused()
    {
        using var engine = new WasapiAudioEngine();

        Assert.False(engine.PushAudio("no-such-strip", new float[960]));
    }

    [Fact]
    public void EmptyAudioIsNotAccepted()
    {
        using var engine = new WasapiAudioEngine();
        string id = engine.EnsureGeneratedInput("TTS");

        Assert.False(engine.PushAudio(id, ReadOnlySpan<float>.Empty));
    }

    #endregion

    /// <summary>
    /// Неназначенный стрип не маршрутизируется: у него нет источника, и звука
    /// просто не существует. Проверка нужна, потому что генерируемый стрип —
    /// единственное исключение из этого правила, и его легко принять за общий случай.
    /// </summary>
    [Fact]
    public void AnUnassignedStripIsNotMarkedAsGenerated()
    {
        using var engine = new WasapiAudioEngine();

        engine.AddInput();

        var strip = Assert.Single(engine.Inputs);
        Assert.False(strip.IsGenerated);
        Assert.False(strip.IsAvailable);
        Assert.Equal("", strip.DeviceId);
    }
}
