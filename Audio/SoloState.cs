namespace SoundMeeter.Audio;

/// <summary>
/// Разделяемое состояние логики "Solo": если хотя бы один стрип группы
/// находится в Solo, несоло-стрипы замолкают. Флаги пишутся из UI-потока
/// (через движок), читаются аудио-потоком каждый буфер.
/// </summary>
public sealed class SoloState
{
    public volatile bool AnyInputSolo;
    public volatile bool AnyOutputSolo;
}