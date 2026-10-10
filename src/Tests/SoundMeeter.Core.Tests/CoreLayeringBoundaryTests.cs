using SoundMeeter.Core.Contract;
using SoundMeeter.Core.Presentation.ViewModels;
using SoundMeeter.Core.UseCases;
using System.Reflection;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Граница между сценариями ядра и его представлением — единственное место,
/// где в этом проекте две стороны действительно могли бы слипнуться, и единственная,
/// которую нельзя проверить компилятором.
///
/// ПОЧЕМУ ГЛАВНОЕ ОКНО РАНЬШЕ ЛЕЖАЛО В СЦЕНАРИЯХ. MainViewModel была
/// одновременно оркестратором и ViewModel: держала коллекции полос для разметки и
/// сама создавала их элементы — девять типов в трёх partial-файлах, плюс ещё три
/// внутри самой InputChannelViewModel. Дерево было взаимно зависимым, и сценарий
/// не мог обойтись без представления, а представление не собиралось без сценария.
/// Тогда это записали как осознанное исключение в CoreLayeringIntentTests.
///
/// ЧТО СДЕЛАНО. Оркестрация вынесена в MixerCoordinator (движок, таймер метров,
/// таймер сохранения, пресет, импорт и экспорт), AppBehaviourService (трей и
/// автозапуск) и давно существовавший StripProvider. Осталось то, что по
/// существу представление: коллекции для разметки и команды. Связь между ними —
/// три уведомления и один вызов PublishStrips; ни одна сторона не знает имён
/// типов другой.
///
/// ПРОВЕРЯЕТСЯ ПО МЕТАДАННЫМ СБОРКИ, А НЕ ПО .CSPROJ. MSBuild выбрасывает
/// неиспользуемые ссылки, и проверка csproj ловила бы объявленное, а не
/// случившееся. Настоящая связь видна в списке ссылок уже собранной сборки.
///
/// Эти проверки падают не на опечатку, а на попытку вернуть то, что только что
/// разобрали: сценарий, знающий о ViewModel, немедленно потянул бы за собой всю
/// разметку обратно в модуль логики.
/// </summary>
public class CoreLayeringBoundaryTests
{
    /// <summary>
    /// Главное правило. Сценарии не знают о представлении — ни прямо, ни через
    /// три уровня вниз. Проверка на одно имя, а не обход графа: обход ловил бы
    /// нарушения глубже, но и падал бы на каждом новом модуле, потому что
    /// представление обязано тянуть их за собой.
    /// </summary>
    [Fact]
    public void TheScenariosLayerDoesNotDependOnThePresentationLayer()
    {
        var scenarios = typeof(MixerCoordinator).Assembly;

        Assert.DoesNotContain("SoundMeeter.Core.Presentation",
            scenarios.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty));
    }

    /// <summary>
    /// То же, но с другой стороны и строже: ни один публичный член оркестратора
    /// не может называть тип представления. Проверка по сигнатуре ловит случай,
    /// когда тип проведёт по полю или возвращаемому значению — такое компилятор
    /// пропустит, если обратная ссылка появится позже и постепенно.
    /// </summary>
    [Fact]
    public void NoOrchestrationMemberNamesAPresentationType()
    {
        var forbidden = typeof(MainViewModel).Namespace!;

        foreach (var type in new[] { typeof(MixerCoordinator), typeof(AppBehaviourService), typeof(StripProvider) })
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (var t in NamedTypesOf(member))
                    Assert.NotEqual(forbidden, t.Namespace);
            }
        }
    }

    /// <summary>
    /// Сценарии принимают и отдают стрипы только интерфейсами. Раньше здесь стоял
    /// <c>IEnumerable&lt;InputChannelViewModel&gt;</c>, и это была та самая ниточка,
    /// которая не давала слоям разойтись: имя ViewModel'а в сигнатуре сценария —
    /// уже зависимость.
    /// </summary>
    [Fact]
    public void TheStripRegistrySpeaksInContractsRatherThanViewModels()
    {
        var reset = typeof(StripProvider).GetMethod(nameof(StripProvider.Reset));

        Assert.NotNull(reset);
        foreach (var parameter in reset!.GetParameters())
        {
            Assert.True(parameter.ParameterType.IsGenericType,
                $"{nameof(StripProvider)}.{nameof(StripProvider.Reset)} принимает {parameter.ParameterType.Name} — " +
                "конкретный тип ViewModel в сигнатуре сценария и есть та зависимость, ради которой слои и разводят");

            var argument = parameter.ParameterType.GetGenericArguments()[0];
            Assert.True(argument.IsInterface, $"Ожидался интерфейс, а не {argument.Name}");
        }
    }

    /// <summary>
    /// Где именно живут ViewModel'ы микшера. Список придётся править руками, и
    /// это сделает пропуск заметным: новый полосный ViewModel, созданный по
    /// образцу соседа, попадёт сюда же.
    /// </summary>
    [Fact]
    public void EveryMixerViewModelLivesInThePresentationLayer()
    {
        Type[] viewModels =
        [
            typeof(MainViewModel),
            typeof(LanguageOptionViewModel),
            typeof(InputChannelViewModel),
            typeof(OutputBusViewModel),
            typeof(OutputOptionViewModel),
            typeof(FuncButtonViewModel),
            typeof(FuncTargetViewModel),
            typeof(StripEffectViewModel),
            typeof(EqualizerViewModel),
            typeof(EqBandViewModel),
            typeof(AppViewModel),
            typeof(InstalledAppViewModel),
            typeof(ConfiguredAppViewModel),
            typeof(AudioDeviceViewModel),
        ];

        foreach (var type in viewModels)
            Assert.Equal("SoundMeeter.Core.Presentation.ViewModels", type.Namespace);
    }

    /// <summary>
    /// Обратная сторона того же разделения: в слое сценариев не осталось ничего,
    /// что можно назвать представлением.
    /// </summary>
    [Fact]
    public void TheScenariosLayerHoldsNoViewModels()
    {
        // LocalizedViewModel живёт в контракте как базовый класс — им пользуется и
        // представление, и логика, отличать надо по конкретным классам микшера.
        var leftovers = typeof(MixerCoordinator).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace == "SoundMeeter.Core.UseCases")
            .Where(t => t.Name.EndsWith("ViewModel", StringComparison.Ordinal)
                     || t.Name.EndsWith("ViewModels", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(leftovers);
    }

    /// <summary>
    /// Список стрипов нужен MIDI, доку и озвучке, и он не должен быть самой VM.
    /// Иначе MainViewModel → DockController → IStripProvider → MainViewModel
    /// замкнулся бы, и контейнер не смог бы собрать ни один из объектов.
    /// Проверяется именно отсутствие интерфейса у главной VM: пока она его
    /// реализует, любой, кто захочет подставить её в контейнер как IStripProvider,
    /// сделает это без единой ошибки компиляции — и приложение упадёт на старте.
    /// </summary>
    [Fact]
    public void TheStripProviderIsNotTheMainViewModel()
    {
        Assert.Equal("SoundMeeter.Core.UseCases", typeof(StripProvider).Namespace);
        Assert.True(typeof(IStripProvider).IsAssignableFrom(typeof(StripProvider)));

        Assert.False(typeof(IStripProvider).IsAssignableFrom(typeof(MainViewModel)),
            "MainViewModel снова реализует IStripProvider — контейнер сможет подставить " +
            "её вместо StripProvider и замкнёт цикл разрешения зависимостей");
    }

    private static IEnumerable<Type> NamedTypesOf(MemberInfo member)
    {
        switch (member)
        {
            case PropertyInfo property:
                yield return property.PropertyType;
                break;
            case FieldInfo field:
                yield return field.FieldType;
                break;
            case MethodInfo method:
                yield return method.ReturnType;
                foreach (var parameter in method.GetParameters())
                    yield return parameter.ParameterType;
                break;
            case ConstructorInfo constructor:
                foreach (var parameter in constructor.GetParameters())
                    yield return parameter.ParameterType;
                break;
        }
    }
}