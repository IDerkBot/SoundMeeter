namespace SoundMeeter.Audio;

/// <summary>
/// Биквад-фильтр второго порядка (RBJ cookbook), Direct Form I.
///
/// Структура, а не класс: фильтры живут в массивах и обрабатываются на
/// захватывающем потоке по одному сэмплу. Состояние лежит рядом с
/// коэффициентами, поэтому обращение к нему — обычная загрузка полей, а ссылка
/// на объект в этом цикле стоила бы дороже самого фильтра.
///
/// Тип общий для всех, кому нужен биквад: формантные пики денойзера и полосы
/// эквалайзера считаются по одной и той же формуле, а не по двум копиям,
/// разъезжающимся при правке любой из них.
///
/// <para><b>Инвариант: коэффициенты заданы до обработки.</b> <c>default</c>
/// фильтра — это нули, то есть фильтр, гасящий сигнал в ноль, а не
/// пропускающий его насквозь. Считать <see cref="Process"/> можно только после
/// одного из методов <c>Set…</c>.</para>
///
/// <para><b>Смена коэффициентов не сбрасывает состояние.</b> Фильтр, который уже
/// звучит, должен продолжить ту же нить: обнуление накопленного обрывает её
/// разрывом. Сбрасывает состояние вызывающий — там, где разрыв действительно
/// нужен.</para>
/// </summary>
public struct BiquadFilter
{
    /// <summary>Ниже этого значения числа считаются денормалями и обнуляются.
    /// Порог примерно −400 дБ: заведомо неслышно, но состояние фильтра затухает
    /// геометрически и секунды после конца сигнала гоняет subnormal-арифметику
    /// на захватывающем потоке (то же рассуждение, что у
    /// <c>DenoiserDsp.Sanitize</c>).</summary>
    private const float DenormalFloor = 1e-20f;

    private float _b0;
    private float _b1;
    private float _b2;
    private float _a1;
    private float _a2;

    private float _x1;
    private float _x2;
    private float _y1;
    private float _y2;

    /// <summary>
    /// Пик с усилением/ослаблением <paramref name="gainDb"/> на частоте
    /// <paramref name="freq"/> (RBJ, A = 10^(gainDb/40)).
    /// </summary>
    public void SetPeaking(float freq, float gainDb, float q, float sampleRate)
    {
        float w0 = Omega(freq, sampleRate);
        float a = MathF.Pow(10f, gainDb / 40f);
        float alpha = MathF.Sin(w0) / (2f * q);
        float cos = MathF.Cos(w0);

        SetNormalized(
            1f + alpha * a,
            -2f * cos,
            1f - alpha * a,
            1f + alpha / a,
            -2f * cos,
            1f - alpha / a);
    }

    /// <summary>
    /// Фильтр верхних частот: срез снизу. <paramref name="q"/> = 0.707 даёт
    /// 12 дБ/окт (Баттерворт).
    /// </summary>
    public void SetHighPass(float freq, float q, float sampleRate)
    {
        float w0 = Omega(freq, sampleRate);
        float alpha = MathF.Sin(w0) / (2f * q);
        float cos = MathF.Cos(w0);

        SetNormalized(
            (1f + cos) / 2f,
            -(1f + cos),
            (1f + cos) / 2f,
            1f + alpha,
            -2f * cos,
            1f - alpha);
    }

    /// <summary>
    /// Фильтр нижних частот: срез сверху. <paramref name="q"/> = 0.707 даёт
    /// 12 дБ/окт.
    /// </summary>
    public void SetLowPass(float freq, float q, float sampleRate)
    {
        float w0 = Omega(freq, sampleRate);
        float alpha = MathF.Sin(w0) / (2f * q);
        float cos = MathF.Cos(w0);

        SetNormalized(
            (1f - cos) / 2f,
            1f - cos,
            (1f - cos) / 2f,
            1f + alpha,
            -2f * cos,
            1f - alpha);
    }

    /// <summary>
    /// «Прозрашка»: коэффициенты единичного усиления. Нужна для срезов, которыми
    /// пользователь не хочет ничего резать: ФВЧ на 20 Гц и тем более ФНЧ на
    /// 20 кГц при 48 кГц всё равно слегка правят сигнал, а «выключено» должно
    /// значить ровно «не трогаю».
    /// </summary>
    public void SetPassThrough() => SetNormalized(1f, 0f, 0f, 1f, 0f, 0f);

    /// <summary>Обнулить накопленное состояние (смена источника, явный сброс).</summary>
    public void ResetState()
    {
        _x1 = _x2 = _y1 = _y2 = 0f;
    }

    /// <summary>Один сэмпл на вход, один на выход. Состояние живёт между вызовами.</summary>
    public float Process(float x)
    {
        float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;

        // Два подряд ушедших в денормали отсчёта — сигнала нет, а состояние ещё
        // могло бы полость с ними доиграть; обнуляем на этой границе.
        if (MathF.Abs(y) < DenormalFloor && MathF.Abs(_y2) < DenormalFloor)
            _x1 = _x2 = _y1 = _y2 = 0f;

        return y;
    }

    /// <summary>
    /// Усиление фильтра на частоте, дБ. Считается в double: у ФВЧ на низкой
    /// частоте числитель — разность почти равных величин, и в float он
    /// теряет знак.
    ///
    /// Нужен окну настроек: кривая рисуется той же формулой, по которой
    /// считается сигнал, поэтому нарисованное и слышимое не расходятся.
    /// </summary>
    public float MagnitudeDb(float freq, float sampleRate)
    {
        double w = 2.0 * Math.PI * ClampFreq(freq, sampleRate) / sampleRate;
        double c1 = Math.Cos(w), c2 = Math.Cos(2.0 * w);
        double s1 = Math.Sin(w), s2 = Math.Sin(2.0 * w);

        double numRe = _b0 + _b1 * c1 + _b2 * c2;
        double numIm = -(_b1 * s1 + _b2 * s2);
        double denRe = 1.0 + _a1 * c1 + _a2 * c2;
        double denIm = -(_a1 * s1 + _a2 * s2);

        double num = numRe * numRe + numIm * numIm;
        double den = denRe * denRe + denIm * denIm;
        if (den <= 1e-30 || num <= 0) return -200f;

        return (float)(10.0 * Math.Log10(num / den));
    }

    private static float Omega(float freq, float sampleRate) =>
        2f * MathF.PI * ClampFreq(freq, sampleRate) / sampleRate;

    /// <summary>
    /// Частота в рабочем диапазоне. Выше Найквиста формулы RBJ дают фильтр с
    /// неустойчивыми коэффициентами, поэтому такое значение — ошибка входных
    /// данных, а не повод отдать их в арифметику.
    /// </summary>
    private static float ClampFreq(float freq, float sampleRate) =>
        float.IsFinite(freq) ? Math.Clamp(freq, 1f, sampleRate * 0.499f) : 1f;

    /// <summary>Коэффициенты с приведением к a0 = 1 (так их и считают в АЧХ).</summary>
    private void SetNormalized(float b0, float b1, float b2, float a0, float a1, float a2)
    {
        if (!float.IsFinite(a0) || MathF.Abs(a0) < 1e-20f)
        {
            SetPassThrough();
            return;
        }

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }
}