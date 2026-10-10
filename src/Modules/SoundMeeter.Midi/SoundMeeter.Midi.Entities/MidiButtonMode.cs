namespace SoundMeeter.Midi.Entities
{
    /// <summary>Режим обработки кнопочной MIDI-привязки.</summary>
    public enum MidiButtonMode
    {
        /// <summary>Обычная кнопка: переключение параметра по фронту нажатия.</summary>
        Toggle = 0,

        /// <summary>PTT по удержанию: пока нажато — параметр активен, на отпускание — возврат.</summary>
        Hold = 1,

        /// <summary>
        /// Кнопка с фиксацией (latching): контроллер шлёт не нажатие, а состояние
        /// «включено/выключено», поэтому параметр ставится ровно в это состояние.
        /// </summary>
        Latch = 2
    }
}
