using System.Runtime.InteropServices;

namespace SoundMeeter.Audio;

/// <summary>
/// Прямая привязка к нативной библиотеке RNNoise (rnnoise.dll из пакета
/// YellowDogMan.RRNoise.NET — managed-обёртка <c>RNNoise.NET.Denoiser</c> не
/// используется, см. <see cref="DenoiserDsp"/>).
///
/// Почему не обёртка: она скрывает внутреннее кольцо буфера и добавляет
/// собственные 480 сэмплов задержки поверх задержки RNNoise, из-за чего
/// «сухой» и «мокрый» сигналы расходятся по времени на 10 мс и кросфейд
/// между ними даёт гребенку. Прямой вызов даёт точный контракт:
/// ровно 480 входных сэмплов -> ровно 480 выходных, плюс возвращает
/// вероятность речи (VAD) для индикации/автоматики.
///
/// Сигнал на входе/выходе ожидается в масштабе RNNoise (±32768),
/// т.е. нормализованный float надо умножить на <see cref="SignalScale"/>.
/// </summary>
internal static unsafe class RnNoiseInterop
{
    /// <summary>Длина кадра RNNoise: 10 мс при 48 кГц.</summary>
    public const int FrameSize = 480;

    /// <summary>Единственная частота, на которой работает модель RNNoise.</summary>
    public const int SampleRate = 48000;

    /// <summary>Масштаб нормализованного float в масштаб RNNoise.</summary>
    public const float SignalScale = 32767f;

    /// <summary>Обратный масштаб: результат RNNoise -> нормализованный float.</summary>
    public const float SignalScaleInv = 1f / 32768f;

    private const string Library = "rnnoise";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rnnoise_create(IntPtr model);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rnnoise_destroy(IntPtr state);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern float rnnoise_process_frame(IntPtr state, float* output, float* input);

    /// <summary>
    /// Создаёт состояние модели со встроенными весами.
    /// </summary>
    /// <remarks>
    /// В <c>rnnoise_create</c> обязательно передаётся <c>IntPtr.Zero</c>:
    /// ненулевой указатель трактуется как адрес сериализованной модели, и
    /// библиотека читает из него ~18 КБ. Если подсунуть буфер меньшего размера,
    /// состояние получается мусорным: шум не подавляется, а обращение к нему
    /// падает с нарушением доступа.
    /// </remarks>
    public static IntPtr Create() => rnnoise_create(IntPtr.Zero);

    public static void Destroy(IntPtr state)
    {
        if (state != IntPtr.Zero)
            rnnoise_destroy(state);
    }

    /// <summary>
    /// Обрабатывает ровно один кадр. <paramref name="input"/> и
    /// <paramref name="output"/> — массивы длиной не меньше <see cref="FrameSize"/>.
    /// Возвращает вероятность речи (0 = шум, 1 = речь).
    /// </summary>
    public static float ProcessFrame(IntPtr state, float[] input, float[] output)
    {
        fixed (float* pin = input)
        fixed (float* pout = output)
            return rnnoise_process_frame(state, pout, pin);
    }
}
