using SoundMeeter.ChangeLanguage.Presentation;
using SoundMeeter.Update.Presentation.Events;
using SoundMeeter.ViewModels;
using SoundMeeter.Views.Controls;
using System.Windows;
using System.Windows.Controls;

namespace SoundMeeter.Views;

/// <summary>
/// Корневой вид главного окна: собирает панель инструментов, ленту стрипов
/// и панели приложений. Логика и разметка частей — в Views/Controls.
/// DataContext — MainViewModel (задаётся окном).
/// </summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        Language = LocResources.XmlLanguage;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Отписываемся от предыдущего ViewModel, чтобы не держать его в памяти.
        if (e.OldValue is MainViewModel oldVm)
            oldVm.InstallRequested -= OnInstallRequested;

        // Подписываемся к новому ViewModel, когда DataContext уже установлен.
        if (e.NewValue is MainViewModel newVm)
            newVm.InstallRequested += OnInstallRequested;
    }

    private void OnInstallRequested(object? sender, UpdateInstallRequestedEventArgs e)
    {
        MixerUi.ShowUpdateWindow(this, e.Update);
    }
}