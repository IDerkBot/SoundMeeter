using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Один выбираемый выход для стрипа (чекбокс в попапе OUT/VIRT).
/// Переключение сразу применяет роутинг в движке.
/// </summary>
public partial class OutputOptionViewModel : ObservableObject
{
    private readonly Action<OutputOptionViewModel> _onChanged;
    private readonly Action<OutputOptionViewModel> _onHidden;

    public string BusId { get; }
    public string Name { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public OutputOptionViewModel(string busId, string name, bool isEnabled, Action<OutputOptionViewModel> onChanged, Action<OutputOptionViewModel> onHidden)
    {
        BusId = busId;
        Name = name;
        _isEnabled = isEnabled;
        _onChanged = onChanged;
        _onHidden = onHidden;
    }

    /// <summary>Скрывает это устройство (шину) из всех стрипов и попапов.</summary>
    [RelayCommand]
    private void Hide()
    {
        IsEnabled = false;
        _onHidden(this);
    }

    partial void OnIsEnabledChanged(bool value) => _onChanged(this);
}