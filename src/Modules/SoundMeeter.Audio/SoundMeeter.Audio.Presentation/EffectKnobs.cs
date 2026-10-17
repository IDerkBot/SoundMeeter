namespace SoundMeeter.Audio.Presentation;

/// <summary>
/// Заводит крутилку параметра эффекта со всеми её привязками к пресету.
///
/// Фабрика отдельная от <see cref="StripEffectViewModel.Knobs"/>, потому что
/// крутилки заводят не только эффекты с попапом: окно эквалайзера собирает свои
/// ползунки иначе, но диапазоны, шаг сброса и отметка «пресет изменён» должны
/// вести себя у всех одинаково.
/// </summary>
internal static class EffectKnobs
{
    public static EffectKnobViewModel Create(
        string nameKey,
        string unit,
        string format,
        double min,
        double max,
        float defaultValue,
        Func<float> get,
        Action<float> set,
        Action markDirty,
        bool logarithmic = false) =>
        new(get, value =>
        {
            set(value);
            markDirty();
        })
        {
            NameKey = nameKey,
            Unit = unit,
            Format = format,
            Minimum = min,
            Maximum = max,
            DefaultValue = defaultValue,
            IsLogarithmic = logarithmic
        };
}