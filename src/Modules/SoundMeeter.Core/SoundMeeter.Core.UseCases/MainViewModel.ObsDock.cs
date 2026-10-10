using SoundMeeter.Docking.UseCases;

namespace SoundMeeter.Core.UseCases;

// Док-панель OBS вынесена в модуль SoundMeeter.Docking: оркестрация — в
// DockController (SoundMeeter.Docking.UseCases), окно настроек — в
// SoundMeeter.Docking.Presentation.
//
// Раньше здесь стояли двадцать делегирующих членов на DockViewModel. Ни один
// не вызывался извне: единственный потребитель — окно настроек, и оно брало
// сам ViewModel. Двадцать обёрток только размножали связность — ядро знало
// про ViewModel-модуля, и этим замыкало цикл, из-за которого представление
// дока нельзя было вынести из UseCases.
//
// Ядру от дока нужно ровно пять вещей, и все адресные: поднять сервер на
// старте, применить настройки при импорте, публиковать снимок по таймеру
// метров и освободиться при закрытии.
public partial class MainViewModel
{
    /// <summary>Модуль док-панели OBS (для окна настроек).</summary>
    public DockController Dock => _dock;
}