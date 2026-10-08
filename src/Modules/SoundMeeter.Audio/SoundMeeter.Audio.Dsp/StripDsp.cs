using SoundMeeter.Models;

namespace SoundMeeter.Audio;
/// <summary>
/// Последовательность обработки входного стрипа (SM-B05), собираемая один раз на
/// источник: <c>денойзер → эквалайзер → компрессор → trim → задержка →
/// реверберация</c>.
///
/// Место в цепочке выбрано по двум правилам:
///
/// * всё, что стоит ДО разветвления на шины, считается один раз на пакет, а не
///   в каждой посылки — иначе компрессор и реверберация считались бы заново для
///   каждой шины, а посылки с разной громкостью звучали бы по-разному;
/// * эффекты с задержкой (задержка, реверберация) идут последними, иначе
///   компрессор слышал бы их эхо и сжимал не голос, а хвост.
///
/// Эквалайзер стоит перед компрессором по обратной причине: полосы и срезы
/// правят форму сигнала, и компрессор должен видеть уже выправленный уровень.
/// Иначе подъём середины приводил бы к тому, что голос после компрессии снова
/// становился тише относительно остальных полос.
///
/// Параметры всех блоков читаются из модели на каждом пакете, поэтому любая
/// крутилка слышна сразу, без пересоздания аудиопотока. Блоки с состоянием
/// (огибающая компрессора, фильтры эквалайзера, линии задержки и реверберации)
/// живут между пакетами: их сброс при включении источника даёт старт «с нуля»
/// вместо продолжения прерванного хвоста.
/// </summary>
public sealed class StripDsp
{
    private readonly EqualizerDsp _equalizer;
    private readonly CompressorDsp _compressor;
    private readonly FxGainDsp _gain;
    private readonly DelayDsp _delay;
    private readonly ReverbDsp _reverb;

    public StripDsp(InputChannelModel model)
    {
        _equalizer = new EqualizerDsp(model);
        _compressor = new CompressorDsp(model);
        _gain = new FxGainDsp(model);
        _delay = new DelayDsp(model);
        _reverb = new ReverbDsp(model);
    }

    /// <summary>Обрабатывает пакет на месте. Длина и число кадров не меняются.</summary>
    public void Process(float[] stereo, int frames)
    {
        if (stereo.Length <= 0 || frames <= 0) return;

        _equalizer.Process(stereo, frames);
        _compressor.Process(stereo, frames);
        _gain.Process(stereo, frames);
        _delay.Process(stereo, frames);
        _reverb.Process(stereo, frames);
        Sanitize(stereo, frames);
    }

    /// <summary>
    /// Сброс состояния блоков: новый запуск источника не должен продолжать
    /// старый хвост задержки/реверберации, старое состояние компрессора и старые
    /// фильтры эквалайзера.
    /// </summary>
    public void Reset()
    {
        _equalizer.Reset();
        _compressor.Reset();
        _gain.Reset();
        _delay.Reset();
        _reverb.Reset();
    }

    /// <summary>
    /// Последний рубеж перед кольцевым буфером: NaN/Inf гасятся, амплитуда
    /// ограничивается. Реверберация с длинным хвостом и feedback 0.9 способна
    /// разогнать пакет, а NaN в кольце распространяется на все посылки стрипа и
    /// глушит канал целиком (то же рассуждение, что в InputSource.ApplyGain).
    /// </summary>
    private static void Sanitize(float[] buffer, int frames)
    {
        int samples = frames * 2;
        if (samples > buffer.Length) samples = buffer.Length;

        for (int i = 0; i < samples; i++)
        {
            float v = buffer[i];
            if (v > -4f && v < 4f) continue;
            buffer[i] = float.IsFinite(v) ? Math.Clamp(v, -4f, 4f) : 0f;
        }
    }
}
