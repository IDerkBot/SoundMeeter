using SoundMeeter.ChangeLanguage.UseCases;

namespace SoundMeeter.Midi.Entities
{
    /// <summary>Описание параметра стрипа, доступного для MIDI-привязки.</summary>
    public sealed class MidiParameterDescriptor
    {
        public string Key { get; init; } = "";

        /// <summary>Ключ подписи в <c>Resources/Strings.resx</c> (SM-C07).</summary>
        public string LabelKey { get; init; } = "";

        /// <summary>
        /// Подпись параметра на текущем языке. Считается, а не хранится: смена языка
        /// не должна требовать пересоздания дескрипторов (они статические).
        /// </summary>
        public string Label => Loc.Get(LabelKey);

        public bool ForInput { get; init; }
        public bool ForBus { get; init; }
        public MidiParamShape Shape { get; init; }
        public float Min { get; init; }
        public float Max { get; init; }
    }
}
