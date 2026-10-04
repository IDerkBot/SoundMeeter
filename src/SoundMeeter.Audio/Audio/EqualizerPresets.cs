using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Готовая кривая эквалайзера: десять полос и общий makeup-gain.
///
/// Пресет трогает только форму сигнала — полосы и makeup-gain. Срезы он не
/// трогает намеренно: срез снизу обычно ставят один раз, чтобы убрать гул, и
/// молча сбрасывать его вместе с тембром значило бы ломать настройку, которой
/// пользователь не касался.
/// </summary>
public sealed class EqualizerPreset
{
    /// <summary>Ключ названия в <c>Resources/Strings.resx</c> (SM-C07).</summary>
    public string Key { get; }

    /// <summary>Общий makeup-gain пресета, дБ. Подбирается вручную под суммарный
    /// подъём полос, а не считается на лету: предсказуемое «-2 дБ» в списке полезнее
    /// неожиданно скакнувшей компенсации.</summary>
    public float PreampDb { get; }

    /// <summary>Усиление полос, дБ, ровно <see cref="InputChannelModel.EqBandCount"/> штук.</summary>
    public float[] BandGainsDb { get; }

    public EqualizerPreset(string key, float preampDb, params float[] bandGainsDb)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(bandGainsDb);

        // Число полос в пресете — не украшение: DSP и окно настроек читают
        // настройки по номеру полосы, и пресет другого размера тихо разъехался бы
        // с ними. Проверка на старте приложения, а не при первом применении.
        if (bandGainsDb.Length != InputChannelModel.EqBandCount)
            throw new ArgumentException(
                $"Пресет {key}: полос {bandGainsDb.Length}, ожидалось {InputChannelModel.EqBandCount}.",
                nameof(bandGainsDb));

        Key = key;
        PreampDb = preampDb;
        BandGainsDb = bandGainsDb;
    }

    /// <summary>Накатить пресет на канал файла настроек.</summary>
    public void ApplyTo(InputChannelModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        for (int i = 0; i < BandGainsDb.Length; i++)
            model.SetEqBand(i, BandGainsDb[i]);

        model.EqPreampDb = PreampDb;
    }
}

/// <summary>
/// Каталог пресетов кривой эквалайзера стрипа.
///
/// Полосы те же, что и в <see cref="InputChannelModel.EqBandFrequencies"/>:
/// 31, 62, 125, 250, 500 Гц, 1, 2, 4, 8, 16 кГц. Порядок в списке совпадает с
/// порядком полос в модели — читать кривую в коде неудобно, и рано или поздно
/// кто-нибудь переставит.
/// </summary>
public static class EqualizerPresets
{
    public static IReadOnlyList<EqualizerPreset> All { get; } =
    [
        // Подъём середины на 2…4 дБ и спад низа: голос перестаёт бубнить и не
        // теряется на фоне остальных полос.
        new("Sm.Eq.Preset.Vocal", -2f,
            -2f, -3f, -2f, 0f, 2f, 4f, 4f, 3f, 1.5f, 0f),

        // Провал верха и подъём низа: тёплый, «студийный» голос. Makeup нужен
        // обязательно, иначе суммарно кривая уходит в плюс.
        new("Sm.Eq.Preset.Warm", -1.5f,
            2f, 2.5f, 2f, 1f, 0f, -1f, -2f, -2.5f, -1.5f, 0f),

        // Обратная предыдущей: подъём верхней середины, спад низа. Для музыки
        // и для голоса на пленке.
        new("Sm.Eq.Preset.Bright", -2f,
            -3f, -3f, -2f, -1f, 0f, 1.5f, 3f, 4.5f, 4f, 2f),

        // Нижний край подтянут, середина оставлена как есть: «вес» у голоса или
        // у баса. Makeup заметный — суммарно кривая в плюс на 4 дБ.
        new("Sm.Eq.Preset.Deep", -4f,
            4f, 5f, 4.5f, 2.5f, 0.5f, 0f, 0f, 0f, 0f, 0f),

        // Универсальный «вырезающий» тон: широкая яма в верхней середине,
        // компенсирующий подъём низов и верха. Музыкальный, не голосовой.
        new("Sm.Eq.Preset.Scooped", -1f,
            2f, 2f, 0f, -3f, -5f, -4f, 0f, 3f, 4f, 3f)
    ];
}