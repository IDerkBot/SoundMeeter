using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.ChangeLanguage.Presentation;
using SoundMeeter.Core.Contract;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Presentation.ViewModels;
using SoundMeeter.Midi.UseCases;
using System.Windows;

namespace SoundMeeter.Midi.Presentation
{
    /// <summary>
    /// Окно привязки MIDI-контроллеров к параметрам стрипов (фейдеры, кнопки, крутилки).
    /// </summary>
    public partial class MidiBindingsWindow : Window
    {
        public MidiBindingsWindow(IServiceProvider services, IDispatcherService dispatcherService)
        {
            InitializeComponent();

            // Сценарии и хранилище берутся из контейнера, а не собираются вручную:
            // окно — последнее место, где сценарий мог бы оказаться собранным
            // неправильно (или не собранным вовсе), и тогда сломалось бы только
            // при открытии этого окна.
            DataContext = new MidiBindingsViewModel(
                services.GetRequiredService<IMidiService>(),
                dispatcherService,
                services.GetRequiredService<SelectMidiDeviceUseCase>(),
                services.GetRequiredService<BindMidiControlUseCase>(),
                services.GetRequiredService<UpdateMidiBindingUseCase>(),
                services.GetRequiredService<ClearMidiBindingUseCase>(),
                services.GetRequiredService<IMidiBindingStore>(),
                services.GetRequiredService<IStripProvider>());

            // Окно создаётся уже после выбора языка, но не наследует Language от
            // родителя (у него своё визуальное дерево) — задаём явно.
            Language = LocResources.XmlLanguage;

            Closed += (_, _) =>
            {
                if (DataContext is MidiBindingsViewModel vm)
                    vm.Dispose();
            };
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}