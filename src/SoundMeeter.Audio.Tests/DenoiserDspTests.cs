using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Денойзер на RNNoise. Тесты бьют по двум дефектам, которые слышны как треск,
/// и оба они молчали: процесс возвращал <c>frames</c> и не бросал исключений.
///
/// * <b>Расстройка «сухого» и «денойзерного» по времени.</b> Кросфейд складывает
///   два сигнала, и если они отстают друг от друга, на середине диапазона они
///   гасят друг друга. При прежней задержке (240 сэмплов вместо 960) ровнотон
///   500 Гц на Noise Remover = 50 % г��с на −64 дБ — то есть на середине
///   крутилки звук пропадал. Тест <see cref="DryAndWetAreAlignedInTime"/>
///   меряет фазу напрямую, а не уровень: фаза не «почти нулевая», она нулевая.
///
/// * <b>Очередь короче пакета.</b> WASAPI отдаёт до 100 мс за раз, очередь
///   держала 40 мс: хвост пакета отбрасывался, а нехватка выхода закрывалась
///   нулями — до 60 % каждого пакета цифровой тишины. Тест
///   <see cref="LongWasapiPacketsDoNotProduceSilence"/> считает эти кадры
///   напрямую, через счётчики денойзера.
/// </summary>
public class DenoiserDspTests
{
    private const int Sr = Signal.SampleRate;

    /// <summary>Пакетов, за которые прогоняется каждый тест. 1 с хватает,
    /// чтобы выйти из начального заполнения очередей.</summary>
    private const int WarmupFrames = Sr;

    [Theory]
    [InlineData(480)]    // 10 мс — пакет NAudio при «нормальном» потоке
    [InlineData(1200)]   // 25 мс
    [InlineData(2400)]   // 50 мс
    [InlineData(4800)]   // 100 мс — дефолт WasapiCapture в NAudio
    [InlineData(9600)]   // 200 мс — пакет разросся из-за просрочки потока
    public void LongWasapiPacketsDoNotProduceSilence(int packetFrames)
    {
        using var dsp = Create(noiseRemover: 100f);
        Run(dsp, Signal.Sine(WarmupFrames * 2, 0.2f, 440f), packetFrames);

        Assert.Equal(0, dsp.OverrunFrames);

        // Нехватка выхода допустима ровно один раз — на первом кадре: RNNoise
        // не может выдать ничего, пока не отобрал целый кадр в 10 мс. Дальше
        // очередь входа переваривает пакет целиком и тишины быть не должно.
        // Прежние 4 кадра очереди давали здесь 20 % (пакет 50 мс) и 60 %
        // (пакет 100 мс) — это 14 400 и 57 600 кадров.
        Assert.True(dsp.UnderrunFrames <= 480,
            $"packet {packetFrames}: {dsp.UnderrunFrames} frames of silence injected");
    }

    [Fact]
    public void DryAndWetAreAlignedInTime()
    {
        // Ровнотон 300 Гц: на нём провал от расстройки виден полностью — при
        // 720 сэмплах сдвига это ровно половина периода, то есть полное гашение.
        const float hz = 300f;
        float[] tone = Signal.Sine(Sr * 2, 0.3f, hz);

        float dryPhase = PhaseOf(tone, Run(Create(0f), tone, 480), hz);
        float wetPhase = PhaseOf(tone, Run(Create(100f), tone, 480), hz);

        float error = MathF.Abs(Wrap(wetPhase - dryPhase));
        Assert.True(error < 20f, $"dry/wet phase error {error:F1} deg at {hz} Hz");
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(700f)]
    [InlineData(1500f)]
    [InlineData(3000f)]
    public void NoiseRemoverSweepNeverDipsBelowTheEndpoints(float hz)
    {
        float[] tone = Signal.Sine(Sr * 2, 0.3f, hz);

        float at0 = Linear(Run(Create(0f), tone, 480));
        float at100 = Linear(Run(Create(100f), tone, 480));

        // Noise Remover — линейный кросфейд, поэтому уровень обязан идти
        // между двумя концами РОВНО по прямой в линейных единицах, а не по
        // прямой в децибелах. Прежняя расстройка по времени давала на 50 %
        // −64 дБ вместо промежуточного значения: не «чуть тише», а тишина.
        for (int pct = 5; pct < 100; pct += 5)
        {
            float a = pct / 100f;
            float expected = ToDb(at0 + (at100 - at0) * a);
            float actual = ToDb(Linear(Run(Create(pct), tone, 480)));
            float deviation = MathF.Abs(actual - expected);

            Assert.True(deviation < 3f,
                $"{hz} Hz at {pct}%: {actual:F2} dB, crossfade predicts {expected:F2} dB (deviation {deviation:F2} dB)");
        }
    }

    [Fact]
    public void LongPacketsDoNotClick()
    {
        float[] tone = Signal.Sine(Sr, 0.3f, 440f);

        // Щелчок от подмешанной тишины не зависит от Noise Remover: он
        // появляется там, где конвейер не успевает за пакетом. Поэтому и
        // проверяется не абсолютная величина, а то, что размер пакета на неё
        // не влияет. Прежние 4 кадра очереди: 0.0178 на 10-мс пакетах и
        // 0.2858 на 100-мс — щелчок в 16 раз громче номинального шага тона.
        float small = MaxStep(Run(Create(100f), tone, 480));
        float large = MaxStep(Run(Create(100f), tone, 4800));

        float nominal = 2f * MathF.PI * 440f * 0.3f / Sr;
        Assert.True(large < nominal * 8f, $"max step {large:F4} on 100 ms packets, tone step {nominal:F4}");
        Assert.True(MathF.Abs(large - small) < nominal,
            $"100 ms packets: step {large:F4}, 10 ms packets: step {small:F4}");
    }

    private static float MaxStep(float[] stereo)
    {
        float max = 0f;
        int n = stereo.Length / 2;
        for (int i = Sr / 2 + 1; i < n; i++)
            max = MathF.Max(max, MathF.Abs(stereo[i * 2] - stereo[(i - 1) * 2]));
        return max;
    }

    [Fact]
    public void SuppressionActuallyRemovesNoise()
    {
        float[] hiss = Noise(Sr * 2, 0.1f);

        float dry = LevelDb(Run(Create(0f), hiss, 480));
        float wet = LevelDb(Run(Create(100f), hiss, 480));

        // RNNoise на чистом шуме должен убрать его целиком; если бы правка
        // выравнивания сломала масштаб или кадры, это первое, что поедет.
        Assert.True(dry - wet > 10f, $"{dry:F2} -> {wet:F2} dB");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(50f)]
    [InlineData(100f)]
    public void MonoInputStaysMono(float noiseRemover)
    {
        // Микрофон моно, и кнопка Mono сводит L+R в центр. Денойзер не имеет
        // права превратить его в два несовпадающих сигнала: оба канала шли через
        // ОДНО rnnoise_state, второй вызов затирал буферы перекрытия STFT
        // первого, и на выходе L и R расходились тем сильнее, чем выше Noise
        // Remover. Прежде расхождение доходило до 134 % от уровня L — это и
        // есть «голос превращается в робота», и это же треск.
        float[] mono = Speech(Sr * 2, 0.3f);
        float[] outSamples = Run(Create(noiseRemover), mono, 480);

        int n = outSamples.Length / 2;
        double diff = 0, reference = 0;
        for (int i = n / 2; i < n; i++)
        {
            double d = outSamples[i * 2] - outSamples[i * 2 + 1];
            diff += d * d;
            reference += (double)outSamples[i * 2] * outSamples[i * 2];
        }

        float ratio = (float)Math.Sqrt(diff / reference);
        Assert.True(ratio < 0.01f, $"L and R differ by {ratio * 100:F2} % of RMS(L) at {noiseRemover:F0}%");
    }

    [Theory]
    [InlineData(0f, 100f)]
    [InlineData(50f, 100f)]
    [InlineData(100f, 25f)]
    [InlineData(100f, 50f)]
    [InlineData(100f, 75f)]
    [InlineData(100f, 100f)]
    public void HighSettingsDoNotClick(float noiseRemover, float dryWet)
    {
        float[] input = Mix(Speech(Sr * 3, 0.3f), Noise(Sr * 3, 0.02f));
        float[] outSamples = Run(new DenoiserDsp(NewModel(noiseRemover, dryWet)), input, 480);

        // Щелчок — широкополосный всплеск, и на гладком сигнале он виден как
        // выброс второй разности: у тона она пропорциональна f² и предсказуема,
        // у щелчка — на порядок выше типичного значения. Сравнение максимума с
        // 99.9-м процентилем устойчиво, в отличие от сравнения с порогом.
        (double typical, double worst) = SecondDifferenceStats(outSamples);
        double ratio = worst / Math.Max(typical, 1e-30);

        Assert.True(ratio < 2.5f,
            $"amount {noiseRemover:F0}% / dry-wet {dryWet:F0}%: max |d2| is {ratio:F1}x the 99.9th percentile");
    }

    [Fact]
    public void MovingTheFormantEqDoesNotClick()
    {
        // Полосы формантного EQ пересчитывались с ResetState() на каждом пакете:
        // обнулённое состояние биквада при ненулевом сигнале — разрыв, то есть
        // щелчок, и не один, а шесть, раз в 10..100 мс, пока крутилка движется.
        var model = NewModel(80f);
        using var dsp = new DenoiserDsp(model);

        float[] input = Speech(Sr * 2, 0.3f);
        var result = new float[input.Length];
        var packet = new float[960];
        int frames = input.Length / 2;
        int step = 0;

        for (int f = 0; f < frames; f += 480)
        {
            // Ползунок едет туда-обратно, как его двигает мышь.
            float sweep = step < 60 ? step * 0.4f : (120 - step) * 0.4f;
            model.DenoiserFormantLowDb = sweep;
            model.DenoiserFormantHighDb = -sweep * 0.5f;
            model.DenoiserFormantGroupDb = sweep * 0.25f;
            step++;

            Array.Copy(input, f * 2, packet, 0, 960);
            dsp.Process(packet, 480);
            Array.Copy(packet, 0, result, f * 2, 960);
        }

        (double typical, double worst) = SecondDifferenceStats(result);
        double ratio = worst / Math.Max(typical, 1e-30);
        Assert.True(ratio < 2.5f, $"EQ sweep: max |d2| is {ratio:F1}x the 99.9th percentile");
    }

    [Fact]
    public void DisabledDenoiserIsBitTransparent()
    {
        float[] input = Signal.Sine(4800, 0.5f);
        using var dsp = new DenoiserDsp(new InputChannelModel { DenoiserEnabled = false });

        float[] through = Run(dsp, input, 480);

        Assert.Equal(input, through);
    }

    [Fact]
    public void ReenablingDoesNotClick()
    {
        // Параметры денойзера переключаются на лету (крутилка в UI), поэтому
        // состояние конвейера после выключения — не «чистый лист», а обнулённая
        // история: сигнал появится только через задержку RNNoise.
        var model = NewModel(100f);
        using var dsp = new DenoiserDsp(model);
        float[] input = Signal.Sine(Sr, 0.3f, 440f);

        Run(dsp, input, 480);                 // активно
        model.DenoiserEnabled = false;
        Run(dsp, input, 480);                 // выключили
        model.DenoiserEnabled = true;
        float[] after = Run(dsp, input, 480);

        // Без нарастания после включения сигнал появился бы скачком от нуля.
        float nominal = 2f * MathF.PI * 440f * 0.3f / Sr;
        for (int i = 1; i < 480; i++)
        {
            float step = MathF.Abs(after[i * 2] - after[(i - 1) * 2]);
            Assert.True(step <= nominal * 4f, $"step {step:F4} at frame {i}");
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Модель с включённым денойзером, сухим эквалайзером и полным Dry/Wet.</summary>
    private static InputChannelModel NewModel(float noiseRemover, float dryWet = 100f) => new()
    {
        DenoiserEnabled = true,
        DenoiserNoiseRemover = noiseRemover,
        DenoiserDryWet = dryWet,
        DenoiserFormantLowDb = 0f,
        DenoiserFormantMidDb = 0f,
        DenoiserFormantHighDb = 0f,
        DenoiserFormantGroupDb = 0f
    };

    private static DenoiserDsp Create(float noiseRemover) => new(NewModel(noiseRemover));

    /// <summary>
    /// Вторая разность по установившейся части сигнала: 99.9-й процентиль
    /// (типичное значение) и максимум (кандидат в щелчки). Считается по
    /// отсчётам одного канала — второй идентичен.
    /// </summary>
    private static (double typical, double worst) SecondDifferenceStats(float[] stereo)
    {
        int n = stereo.Length / 2;
        var sorted = new List<double>(n);
        for (int i = n / 2 + 2; i < n; i++)
            sorted.Add(Math.Abs(stereo[i * 2] - 2 * stereo[(i - 1) * 2] + stereo[(i - 2) * 2]));
        sorted.Sort();
        return (sorted[(int)(sorted.Count * 0.999)], sorted[^1]);
    }

    /// <summary>
    /// Прогон пакетами по <paramref name="packetFrames"/> кадров — так же, как
    /// это делает <c>InputSource</c>. Смещение 0 у каждого пакета: иначе
    /// «задержка» в измерениях была бы артефактом склейки, а не свойством DSP.
    /// </summary>
    private static float[] Run(DenoiserDsp dsp, float[] input, int packetFrames)
    {
        var output = new float[input.Length];
        var packet = new float[packetFrames * 2];
        int frames = input.Length / 2;

        for (int f = 0; f < frames; f += packetFrames)
        {
            int count = Math.Min(packetFrames, frames - f);
            Array.Copy(input, f * 2, packet, 0, count * 2);
            dsp.Process(packet, count);
            Array.Copy(packet, 0, output, f * 2, count * 2);
        }

        return output;
    }

    /// <summary>
    /// Уровень установившейся части сигнала в линейных единицах (RMS).
    /// Считается по второй половине: начало прогона — это задержка конвейера
    /// и нарастание после включения.
    /// </summary>
    private static float Linear(float[] stereo)
    {
        int n = stereo.Length / 2;
        double sum = 0;
        for (int i = n / 2; i < n; i++)
        {
            double v = stereo[i * 2];
            sum += v * v;
        }
        return (float)Math.Sqrt(sum / (n / 2));
    }

    private static float ToDb(float linear) => 20f * MathF.Log10(MathF.Max(linear, 1e-12f));

    private static float LevelDb(float[] stereo) => ToDb(Linear(stereo));

    /// <summary>
    /// Фаза сигнала относительно <paramref name="reference"/> на частоте
    /// <paramref name="hz"/>, в градусах, по установившейся части. Atan2
    /// возвращает радианы, а дальше всё считается в градусах, — перевод
    /// обязателен, иначе порог «20 градусов» молча превращается в «20 радиан».
    /// </summary>
    private static float PhaseOf(float[] reference, float[] stereo, float hz)
    {
        (double re, double im) = Bin(reference, hz);
        (double re2, double im2) = Bin(stereo, hz);
        float refPhase = MathF.Atan2((float)im, (float)re) * RadToDeg;
        float phase = MathF.Atan2((float)im2, (float)re2) * RadToDeg;
        return Wrap(phase - refPhase);
    }

    private const float RadToDeg = 180f / MathF.PI;

    private static (double re, double im) Bin(float[] stereo, float hz)
    {
        int n = stereo.Length / 2;
        double re = 0, im = 0;
        for (int i = n / 2; i < n; i++)
        {
            double a = -2 * Math.PI * hz * i / Sr;
            double v = stereo[i * 2];
            re += v * Math.Cos(a);
            im += v * Math.Sin(a);
        }
        return (re, im);
    }

    private static float Wrap(float degrees)
    {
        while (degrees > 180f) degrees -= 360f;
        while (degrees < -180f) degrees += 360f;
        return degrees;
    }

    private static float[] Noise(int frames, float amplitude)
    {
        var buffer = new float[frames * 2];
        uint seed = 20240u;
        for (int i = 0; i < frames; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            float v = amplitude * ((seed >> 8) / 8388608f - 1f);
            buffer[i * 2] = v;
            buffer[i * 2 + 1] = v;
        }
        return buffer;
    }

    private static float[] Mix(float[] a, float[] b)
    {
        var result = new float[a.Length];
        for (int i = 0; i < a.Length; i++) result[i] = a[i] + b[i];
        return result;
    }

    /// <summary>
    /// Голосоподобный сигнал: основной тон 120 Гц с формантной огибающей,
    /// слоговой амплитудой и глиссандо. Моно по обеим каналам — как настоящий
    /// микрофон.
    /// </summary>
    private static float[] Speech(int frames, float amplitude)
    {
        var buffer = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float t = i / (float)Sr;
            float env = 0.35f + 0.65f * MathF.Max(0f, MathF.Sin(2f * MathF.PI * 3.5f * t));
            float f0 = 120f * (1f + 0.12f * MathF.Sin(2f * MathF.PI * 0.8f * t));
            float v = 0f;
            for (int h = 1; h <= 40; h++)
            {
                float fh = f0 * h;
                if (fh > Sr * 0.45f) break;
                float form = 1f / (1f + MathF.Pow((fh - 700f) / 300f, 2f))
                           + 1f / (1f + MathF.Pow((fh - 1220f) / 350f, 2f))
                           + 0.6f / (1f + MathF.Pow((fh - 2600f) / 400f, 2f));
                v += (form / h) * MathF.Sin(2f * MathF.PI * fh * t + 0.7f * h);
            }
            v *= 0.05f * amplitude / 0.3f * env;
            buffer[i * 2] = v;
            buffer[i * 2 + 1] = v;
        }
        return buffer;
    }
}
