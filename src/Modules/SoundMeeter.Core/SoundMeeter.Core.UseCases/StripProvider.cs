using SoundMeeter.Core.Contract;

namespace SoundMeeter.Core.UseCases;

/// <summary>
/// Общий список стрипов для всего, что ими управляет снаружи: MIDI-модуль,
/// док-панель OBS и модуль синтеза речи.
///
/// Почему это отдельный объект, а не <c>MainViewModel</c>, который тоже
/// реализует <see cref="IStripProvider"/>. Зависимость идёт в обе стороны:
/// MainViewModel создаёт DockViewModel, а тот просит IStripProvider. Если бы в
/// контейнере IStripProvider означал «тот же MainViewModel», получился бы цикл
/// MainViewModel → DockViewModel → IStripProvider → MainViewModel, и контейнер
/// не смог бы собрать ни один из них — приложение падало бы на старте.
///
/// Список наполняет <c>MainViewModel.OnChannelsChanged</c>, то есть он всегда
/// отражает то же, что и его коллекции Inputs/Buses. Отдельного уведомления о
/// смене состава здесь нет намеренно: потребители читают список по требованию
/// (пришёл MIDI-фронт, пришёл запрос дока), а не подписываются на изменения.
/// </summary>
public sealed class StripProvider : IStripProvider
{
    private readonly List<IInputStrip> _inputs = new();
    private readonly List<IOutputStrip> _buses = new();

    public IReadOnlyList<IInputStrip> Inputs => _inputs;

    public IReadOnlyList<IOutputStrip> Buses => _buses;

    /// <summary>
    /// Полностью заменяет содержимое. Пересборка целиком, а не добавление:
    /// MainViewModel на каждом осмотре каталога пересоздаёт ViewModel'ы стрипов,
    /// и оставшиеся в списке старые экземпляры указывали бы на уже выкинутые
    /// модели — привязка продолжала бы писать в несуществующий канал.
    /// </summary>
    public void Reset(
        IEnumerable<InputChannelViewModel> inputs,
        IEnumerable<OutputBusViewModel> buses)
    {
        _inputs.Clear();
        _inputs.AddRange(inputs);

        _buses.Clear();
        _buses.AddRange(buses);
    }
}