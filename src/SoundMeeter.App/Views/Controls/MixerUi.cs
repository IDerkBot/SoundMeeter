using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.ViewModels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SoundMeeter.Views.Controls
{
    /// <summary>
    /// Общие действия для контролов микшера: поиск MainViewModel вверх по
    /// визуальному дереву, перенос приложений (drag-n-drop) и диалоги.
    /// Нужны, потому что контролы-стрипы и панели приложений живут
    /// в отдельных UserControl и не видят DataContext главного окна.
    /// </summary>
    internal static class MixerUi
    {
        /// <summary>Ближайший предок в дереве, у которого в DataContext лежит MainViewModel.</summary>
        public static MainViewModel? FindMainViewModel(DependencyObject? source)
        {
            for (var current = source; current != null; current = GetParent(current))
            {
                if (current is FrameworkElement { DataContext: MainViewModel vm }) return vm;
            }

            return null;
        }

        private static DependencyObject? GetParent(DependencyObject child) =>
            child is Visual or Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);

        /// <summary>Приложение из перетаскиваемых данных (null — мимо или свой тип).</summary>
        public static object? GetDraggedApp(IDataObject data) =>
            data.GetData("AppViewModel") ??
            data.GetData("InstalledAppViewModel") ??
            data.GetData("ConfiguredAppViewModel");

        /// <summary>Приложение можно перетаскивать на стрип, только если известен его .exe.</summary>
        public static bool CanDragApp(object? app) =>
            app is AppViewModel or ConfiguredAppViewModel ||
            app is InstalledAppViewModel installed && !string.IsNullOrWhiteSpace(installed.ExecutablePath);

        public static IDataObject CreateAppData(object app) => new DataObject(app.GetType().Name, app);

        /// <summary>Запуск переноса приложения и сброс подсветки стрипов после него.</summary>
        public static void StartAppDrag(FrameworkElement source, object app, MainViewModel? main)
        {
            try
            {
                DragDrop.DoDragDrop(source, CreateAppData(app), DragDropEffects.Move);
            }
            finally
            {
                ClearDropTargets(main);
            }
        }

        public static void ClearDropTargets(MainViewModel? main)
        {
            if (main == null) return;
            foreach (var strip in main.Inputs) strip.IsDropTarget = false;
        }

        public static void ShowRoutingError(DependencyObject owner, string error) => MessageBox.Show(
            Window.GetWindow(owner), error, Loc.Get("Sm.Routing.DialogCaption"), MessageBoxButton.OK, MessageBoxImage.Error);

        /// <summary>Окно установки релиза (тот же сервис, что и у тихой проверки обновлений).</summary>
        public static void ShowUpdateWindow(DependencyObject owner, UpdateInfo update)
        {
            var updates = App.ServiceProvider.GetRequiredService<IUpdateService>();
            new UpdateWindow(updates, update) { Owner = Window.GetWindow(owner) }.ShowDialog();
        }
    }
}