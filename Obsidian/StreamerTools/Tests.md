## Что появилось

Тестов теперь шесть проектов, **120 тестов, ~6 с, без аудиоустройств и без администратора**
(до разделения на Core/App был один проект `SoundMeeter.Tests` на 48 тестов):

| Проект | Тестов | Файлы |
|---|---|---|
| `SoundMeeter.App.Tests` | 54 | `FuncButtonTests`, `StripEffectTests`, `ParamResetTests`, `AppIconTests`, `ModuleBoundaryTests` + STA-хост |
| `SoundMeeter.Core.Tests` | 11 | `AudioEnginePresetTests`, `MidiBindingTests`, `CoreWpfFreeTests` + фикстуры ViewModel |
| `SoundMeeter.Audio.Tests` | 31 | `StripDspTests`, `SettingsMigratorTests`, `CoreValueTests`, `SampleRingBufferTests`, `AudioModuleBoundaryTests`, `Signal` |
| `SoundMeeter.Update.Tests` | 18 | `AppVersionTests`, выбор ассета релиза |
| `SoundMeeter.ChangeLanguage.Tests` | 6 | все ключи локализации находятся, все языковые файлы полны |

| Файл | Что защищает |
|---|---|
| `FuncButtonTests` | назначение, эксклюзивность, аддитивный режим, возврат базы, метка, кнопка без назначения |
| `AudioEnginePresetTests` | чистка мёртвых Id, глубокое копирование снимка, «перезапуск» |
| `StripDspTests` | компрессор/trim/задержка/реверберация на сигнале, NaN-защита |
| `StripEffectTests` | крутилки, попап, раскладка колонки, клики |
| `MidiBindingTests` | дескрипторы Func, Set/Get/Toggle через приватный диспетчер |
| `ParamResetTests` | сброс всех пяти типов регуляторов, «повторный сброс не пачкает пресет» |
| `ModuleBoundaryTests` | 9 проверок: модули не знают друг о друге, не знают про ядро и приложение, ядро не знает про приложение и UI-модули, приложение знает про ядро, точка входа только у приложения |
| `AppIconTests` | значок по пути и по PID на настоящем файле Windows: непустой, замороженный, кэшируется, на мусоре и на мёртвом PID возвращает null, ConvertBack падает |
| `CoreWpfFreeTests` | ядро не ссылается на WPF ни прямо, ни по цепочке; ядро — библиотека без точки входа |

Инфраструктура — `UiHost` и `VisualTree` живут в `SoundMeeter.App.Tests` (нужна разметка и
настоящий `Application`), `FakeAudioEngine` и `Strips` — в `SoundMeeter.Core.Tests`, откуда
`App.Tests` линкует их исходники (`<Compile Include="..\SoundMeeter.Core.Tests\Infrastructure\...">`
). Дублировать фикстуры нельзя: `FakeAudioEngine` — реализация `IAudioEngine`, и вторая копия
разошлась бы с первой по сигнатуре.

## Граница «ядро не тянет UI» — теперь проверяется

`UseWPF` снят с `SoundMeeter.Core`, и это проверяет `CoreWpfFreeTests` (обход всего графа
ссылок ядра, а не одного уровня — см. историю с `LocResources` в `ChangeLanguage`).
`SoundMeeter.Core.Tests` тоже без `UseWPF`: если в ядре снова появится `System.Windows`,
этот проект даже не соберётся, а тест падать не будет.

Проверено пробой в обе стороны:
* вернул `UseWPF` в Core — `CoreWpfFreeTests.CoreDoesNotDependOnWpf` упал;
* положил в Core файл с `System.Windows.Application` — сборка Core отказалась
  (`CS0234`), то есть границу держит уже и компилятор.

Тонкость, о которой стоит помнить (перенесена из этапа 1): `Assembly.GetReferencedAssemblies()`
показывает сборки, ТИПЫ которых реально используются. Просто добавленный `ProjectReference`
без единого `using` в метаданных не появляется и тестом не ловится — ловится только
настоящая связь. Первая проба «добавил ссылку на TrayIcon» так и осталась зелёной.

## Что выяснилось по дороге

**xUnit v2 не умеет STA.** Пришлось делать `UiHost` — один STA-поток на весь набор. Плюс вылезли три грабли, все воспроизведены в коде комментариями:

- `Lazy<Thread>` с фабрикой `() => new Thread(Start)` **не запускает поток** — UI-тесты висели по 30 с каждый, а падало всё с `NullReferenceException` на `Application.Current.Dispatcher`. Лечится `Start()` внутри фабрики.
- apartment надо ставить **до** `Start()`: xUnit даёт MTA, WPF в MTA `Application` не создаёт.
- вернуть из `Run` объект WPF нельзя — asserts после падают «владелец — другой поток». Первый вариант теста именно так и вытаскивал ToggleButton наружу.

**Одна проверка харнесса была неверной.** Попап FUNC: я ждал 3 `CheckBox`, а их 4 — три строки выходов плюс флажок режима «только свои / дописать». Переписал на подсчёт по `DataContext is FuncTargetViewModel` со сверкой Id.

**`FakeAudioEngine` годится не везде.** Для пресетов он бесполезен: чистка мёртвых Id и глубокое копирование живут внутри настоящего `WasapiAudioEngine`. Там — реальный движок (аудиопоток не открывается, устройства не нужны).

## Одна вещь, которую стоит знать

`dotnet build StreamerTools.slnx` **падает**, и это не связано с нами: `Main.vcxproj` (C++ драйвер кабеля) dotnet CLI не собирает. Было и до этого. Все пять C#-проектов по отдельности собираются в Release без ошибок; тесты зелёные в Debug и Release, три прогона подряд стабильно.

Харнесс из `Temp` удалил — теперь его работа версионируется.

## Что дальше

Пробелы, которые я записал в SM-E02, а не молча оставил: `AppVersion`, `UpdateService` (`FindPayloadRoot`/`Extract`, генерация `.ps1`), `SampleRingBuffer`, `SoloState`, biquad в `DenoiserDsp`, нормализация таблицы маршрутов, `GainDb` → линейный коэффициент. Плюс CI (SM-E01) — сейчас тесты гоняются только вручную.

Дальше по плану шёл пункт 2 — вынести `Audio` + `Models` в `SoundMeeter.Audio` без ссылки на WPF. Скажешь, продолжать?