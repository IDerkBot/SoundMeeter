using SoundMeeter.Framework.Entities;
using SoundMeeter.Infrastructure.Entities;


namespace SoundMeeter.Framework.Presentation.ViewModels;

// Восстановление пресета и сохранение настроек микшера — в MixerCoordinator
// (SoundMeeter.Core.UseCases). Здесь остались только две двери, в которые
// стучится приложение: OnStartup вызывает Restore, окно при закрытии — SaveNow.
public partial class MainViewModel
{
    /// <summary>
    /// Применяет сохранённый пресет на старте приложения и автозапускает движок.
    /// </summary>
    public void Restore(AppSettings? saved) => _coordinator.Restore(saved);

    /// <summary>
    /// Немедленно сохраняет настройки (при закрытии окна).
    /// </summary>
    public void SaveNow() => _coordinator.SaveNow();
}