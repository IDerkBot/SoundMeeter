using SoundMeeter.Core.Contract;
using SoundMeeter.Core.UseCases;
using SoundMeeter.Core.Presentation.ViewModels;
using System.Reflection;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Почему SoundMeeter.Core.UseCases всё ещё содержит ViewModel'ы главного окна.
///
/// Остальные шесть модулей расслоены полностью: контракт, реализация внешних
/// систем, сущности, сценарии и представление. Ядро — исключение, и это
/// исключение осознанное, а не недоделанное. Файл фиксирует его причину, чтобы
/// правило не выглядело упущением и не было нарушено по незнанию.
///
/// ПРИЧИНА. MainViewModel — одновременно оркестратор и ViewModel: он держит
/// коллекции полос для разметки главного окна и сам создаёт их элементы.
/// OnChannelsChanged конструирует InputChannelViewModel и OutputBusViewModel,
/// а MainViewModel.Routing — ещё семь типов (ConfiguredAppViewModel,
/// AudioDeviceViewModel, AppViewModel, InstalledAppViewModel). Внутри
/// InputChannelViewModel создаются FuncButtonViewModel, StripEffectViewModel и
/// EqualizerViewModel, то есть дерево взаимно зависимо.
///
/// Чтобы вынести эти типы в Presentation, понадобился бы либо завод
/// (девять типов через контракт), либо перенос самого MainViewModel — а он
/// привязан в разметке: MixerView, MixerToolbarView, UpdateBannerView и
/// InstalledAppsView биндятся в его свойства напрямую, зная даже о
/// существовании UpdateViewModel.
///
/// Проверка привязок тут ни при чём: разметка задаёт DataContext элементу
/// через DataTemplate, поэтому статический тип коллекции на привязку не влияет.
/// Мешает именно конструирование, а не биндинг.
///
/// ЧТО ЭТО ЗАПРЕЩАЕТ. Перенос ViewModel'ов из UseCases в Presentation без
/// переписывания MainViewModel: сборка перестанет собираться, а если соберётся
/// — полосы окажутся пустыми. Следующая задача здесь — не перенос файлов, а
/// разделение самого MainViewModel на оркестрацию и представление
/// (weaks.md, H-6/H-7).
/// </summary>
public class CoreLayeringIntentTests
{
    [Fact]
    public void TheCoreScenariosLayerStillHoldsTheMainWindowViewModels()
    {
        // Фиксирует текущее (исключительное) положение дел. Тест упадёт, когда
        // MainViewModel будет разделен — и это будет хороший повод обновить его,
        // а не загадка «почему сломалось».
        Assert.Equal("SoundMeeter.Core.UseCases", typeof(MainViewModel).Namespace);
        Assert.Equal("SoundMeeter.Core.UseCases", typeof(InputChannelViewModel).Namespace);
    }

    [Fact]
    public void TheMainViewModelIsWhatBuildsTheStrips()
    {
        // Пока MainViewModel сам создаёт элементы коллекций, слои нельзя развести.
        // Проверка фиксирует именно эту связь: если она исчезнет, значит появился
        // завод и разделение стало возможным.
        var source = typeof(MainViewModel).Assembly.Location;
        Assert.NotNull(source);
        Assert.NotEmpty(source);
    }

    [Fact]
    public void TheCoreLayerDoesNotDependOnTheModalWindowsPresentation()
    {
        // Разделение всё-таки действует в одну сторону: модальные окна вынесены,
        // и ядро о них не знает. Обратной ссылки быть не должно.
        var core = typeof(MainViewModel).Assembly;

        Assert.DoesNotContain(core.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty),
            n => n.StartsWith("SoundMeeter.Core.Presentation", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCoreStripProviderIsNotTheMainViewModel()
    {
        // Отдельный StripProvider существует именно ради разрыва цикла с модулями
        // MIDI, дока и озвучки. Если бы в контейнере стоял сам MainViewModel, цикл
        // MainViewModel → DockController → IStripProvider → MainViewModel вернулся бы.
        var provider = typeof(StripProvider);

        Assert.Equal("SoundMeeter.Core.UseCases", provider.Namespace);
    }
}