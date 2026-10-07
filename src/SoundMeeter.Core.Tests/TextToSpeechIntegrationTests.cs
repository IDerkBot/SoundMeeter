using System.Diagnostics;
using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Services.TextToSpeech;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Сквозная проверка модуля синтеза речи (SM-E01) на настоящем синтезаторе Windows.
///
/// <see cref="Pcm16MonoStreamTests"/> проверяет конвертацию байтов, а
/// <see cref="SyntheticInputSourceTests"/> — темп. Между ними лежит SAPI, и вот
/// здесь проверяется связка: настоящий голос → порции сэмплов → шина микшера.
///
/// Проверка проходит на любой машине с установленным голосом (в Windows есть
/// встроенные, в том числе русские) и пропускается, если голосов нет — на
/// сервере сборки их может не быть, и падать из-за этого нельзя. Звуковой карты
/// и прав администратора не нужно: SAPI работает через собственный вывод.
/// </summary>
public class TextToSpeechIntegrationTests
{
    private const int SampleRate = InputSource.SampleRate;
    private const int Channels = InputSource.Channels;

    /// <summary>Фраза покороче: синтез занимает столько же времени, сколько звучит.</summary>
    private const string Phrase = "Привет.";

    [Fact]
    public void TheRealSynthesizerFillsTheMixerRingAtPlaybackRate()
    {
        var catalog = new SapiVoiceCatalog();
        using var speech = new SapiSpeechEngine();

        if (!catalog.IsAvailable || !speech.IsAvailable) return;

        var voice = catalog.Resolve(null);
        Assert.NotNull(voice);

        var model = new InputChannelModel
        {
            Name = "TTS",
            ChannelName = "TTS",
            IsGenerated = true,
            IsAvailable = true,
        };

        using var source = new SyntheticInputSource(model);

        // Тап, а не кольцо источника: речь идёт мимо кольца (его полоса занята
        // захватом устройства) и складывается с кольцом на выходе стрипа.
        // Подключаем ДО синтеза — очередь не переигрывает новому читателю хвост.
        var tap = new BusTap(
            new SampleRingBuffer(SampleRate * Channels).OpenCursor(),
            model, new BusRouting(), new SoloState(), extra: source.External);

        float peak = 0f;
        var stopwatch = Stopwatch.StartNew();

        speech.Speak(Phrase, new TtsSpeechOptions(voice.Name, 0, 100), chunk =>
            source.PushAudio(chunk), CancellationToken.None);

        // Читаем темпом шины и замечаем пик. Синтез отдал данные раньше, чем они
        // прозвучали, поэтому времени на чтение заведомо хватает.
        var block = new float[SampleRate / 100 * Channels];

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(6))
        {
            tap.Read(block.AsSpan(0, block.Length));
            for (int i = 0; i < block.Length; i++) peak = Math.Max(peak, Math.Abs(block[i]));
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(1) && source.BufferedSeconds == 0) break;

            Thread.Sleep(10);
        }

        // Голос обязан дойти до шины: пустой выход означал бы, что связка
        // SAPI → конвертация → источник → тап развалилась где-то на середине.
        Assert.True(peak > 0.01f, $"Шина осталась немой (пик {peak:F4}) — речь не дошла до источника");

        // И он обязан прозвучать ЦЕЛИКОМ. Это главная проверка: раньше фраза ложилась
        // прямо в кольцо источника, переполняла его мгновенно, и на стрим уходил
        // только последний конец сообщения. Здесь темп задаёт шина, поэтому к концу
        // ожидания очередь обязана быть пуста — иначе фраза не доиграла.
        Assert.True(source.BufferedSeconds == 0,
            $"В очереди осталось {source.BufferedSeconds:F2} с — фраза не прозвучала целиком");
    }

    /// <summary>
    /// Голос, который модуль выбирает сам по умолчанию, обязан быть русским: иначе
    /// стример услышал бы русскую фразу английским голосом и счёл бы модуль
    /// сломанным. На машине без русских голосов проверка осмысленна только
    /// сообщением о том, что их нет.
    /// </summary>
    [Fact]
    public void TheDefaultVoiceChoiceIsRussianOrReportedAsAbsent()
    {
        var catalog = new SapiVoiceCatalog();
        if (!catalog.IsAvailable) return;

        var voice = catalog.Resolve(null);
        Assert.NotNull(voice);

        var russian = catalog.Voices.Where(candidate => candidate.IsRussian).ToList();
        if (russian.Count == 0) return;   // русских голосов в системе нет — выбирать не из чего

        Assert.True(voice.IsRussian,
            $"Без явного выбора выбран «{voice.Name}» ({voice.Culture}), хотя русские голоса есть");
    }

    /// <summary>
    /// Голоса перечисляются один раз и кэшируются: перечисление уходит в SAPI и на
    /// машине с двадцатью голосами занимает заметное время.
    /// </summary>
    [Fact]
    public void TheVoiceCatalogIsCheapToAskRepeatedly()
    {
        var catalog = new SapiVoiceCatalog();
        if (!catalog.IsAvailable) return;

        var stopwatch = Stopwatch.StartNew();
        var first = catalog.Voices;
        for (int i = 0; i < 50; i++) _ = catalog.Voices;
        stopwatch.Stop();

        Assert.Same(first, catalog.Voices);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"50 обращений к кэшу заняли {stopwatch.ElapsedMilliseconds} мс — кэша нет");
    }
}
