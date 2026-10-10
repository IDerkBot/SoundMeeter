namespace SoundMeeter.Midi.Entities
{
    /// <summary>Форма управления параметром.</summary>
    public enum MidiParamShape
    {
        /// <summary>Непрерывный фейдер — CC/PitchWheel маппится на диапазон Min..Max.</summary>
        Fader,

        /// <summary>Непрерывная крутилка — CC маппится на диапазон Min..Max.</summary>
        Knob,

        /// <summary>Кнопка — переключение (toggle) по нажатию.</summary>
        Button
    }
}
