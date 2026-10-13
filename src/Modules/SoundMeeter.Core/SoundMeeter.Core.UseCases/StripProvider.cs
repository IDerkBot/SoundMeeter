using SoundMeeter.Audio.Contract;
using SoundMeeter.Framework.Contract;

namespace SoundMeeter.Framework.UseCases;

/// <summary>
/// Общий список стрипов для всего, что ими управляет снаружи: MIDI-модуль,
/// док-панель OBS и модуль синтеза речи.
///
/// Почему это отдельный объект, а не главная VM. Зависимость идёт в обе стороны:
/// окно дока и модули просят IStripProvider, а наполняет его представление
/// микшера. Если бы IStripProvider в контейнере означал «тот же MainViewModel»,
/// получился бы цикл MainViewModel → DockController → IStripProvider →
/// MainViewModel, и контейнер не смог бы собрать ни один из них — приложение
/// падало бы на старте. Главная VM интерфейс не реализует намеренно, и
/// CoreLayeringBoundaryTests следит, чтобы это не вернулось: проверка на
/// отсутствие интерфейса ловит подмену в контейнере, которую компилятор бы
/// пропустил.
///
/// Список наполняет представление через <see cref="MixerCoordinator.PublishStrips"/>,
/// то есть он всегда отражает то же, что и коллекции главного окна. Отдельного
/// уведомления о смене состава здесь нет намеренно: потребители читают список по
/// требованию (пришёл MIDI-фронт, пришёл запрос дока), а не подписываются на
/// изменения.
///
/// Принимает интерфейсы, а не конкретные ViewModel'ы: ViewModel полосы живёт в
/// слое представления, и назвать его здесь было бы обратной ссылкой.
/// </summary>
public sealed class StripProvider : IStripProvider
{
    private readonly List<IInputStrip> _inputs = new();
    private readonly List<IOutputStrip> _buses = new();

    public IReadOnlyList<IInputStrip> Inputs => _inputs;

    public IReadOnlyList<IOutputStrip> Buses => _buses;

    /// <summary>
    /// Полностью заменяет содержимое. Пересборка целиком, а не добавление:
    /// главная VM на каждом осмотре каталога пересоздаёт ViewModel'ы стрипов,
    /// и оставшиеся в списке старые экземпляры указывали бы на уже выкинутые
    /// модели — привязка продолжала бы писать в несуществующий канал.
    /// </summary>
    public void Reset(
        IEnumerable<IInputStrip> inputs,
        IEnumerable<IOutputStrip> buses)
    {
        _inputs.Clear();
        _inputs.AddRange(inputs);

        _buses.Clear();
        _buses.AddRange(buses);
    }
}