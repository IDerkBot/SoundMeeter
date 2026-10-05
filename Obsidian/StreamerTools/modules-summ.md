# StreamerTools / SoundMeeter — контекст и план работ

Этот файл фиксирует контекст работы агента со мной (пользователем) над модулем SoundMeeter
(«VoiceMeeter-like» микшер) в репозитории StreamerTools. Обновляй его сам во время работы.
Всегда читай заголовок «LAST_ACTION» перед ответом — но помни, что если в сообщении
есть НОВАЯ задача, выполнять надо её.

---

## LAST_ACTION

### Resolved (этот turn) — этап 2 SM-A10: ядро без WPF (UseWPF снят, граница закреплена тестом)
- ЗАДАЧА: «Всё, я закрыл приложение, можешь выполнять этап 2» — то есть убрать WPF из
  `SoundMeeter.Core`, как и было записано планом в конце предыдущего turn.
- ЧТО ЗАКРЫТО ИНТЕРФЕЙСАМИ (по одному на зависимость, все — в Core, реализации в App):
  * ЗНАЧКИ. `BitmapImage`/`ImageSource` жили в `AppViewModel`, `ConfiguredAppViewModel`,
    `InstalledAppViewModel` (у каждого своя копия P/Invoke `SHGetFileInfo`) и в
    `IAudioService.GetAppIcon(uint)`, который НЕ ВЫЗЫВАЛСЯ НИ РАЗУ. Теперь значок
    достаёт UI: `Converters/FileIconConverter` (путь), `Converters/ProcessIconConverter`
    (PID) и единственная копия interop в `Services/ShellIcons.cs` с кэшем по пути.
    Ядро отдаёт только строку `IconPath` или `uint ProcessId`. Мёртвый `GetAppIcon`
    удалён вместе с кэшем и `using System.Drawing`.
  * ПОТОК UI. `IDispatcherService` расширен `Post(Action)` и `HasThreadAccess`; реализация
    `DispatcherService` переехала в App и берёт `Application.Current.Dispatcher` один
    раз в конструкторе. `LocalizedViewModel` больше не знает про `Application.Current`:
    добавлен `protected virtual RunOnUiThread` (по умолчанию сразу). Переопределяют его
    ровно два места — `MainViewModel` и `MidiBindingsViewModel`, потому что только там
    события реально приходят с фонового потока (MIDI-порт и команды дока).
    Обоснование снятия подстраховки в базовом классе: `Loc.LanguageChanged` поднимают
    только `App.OnStartup` и `MainViewModel.SelectLanguage`, оба с UI-потока, поэтому
    ветка «с фонового потока» не срабатывала никогда.
  * ТАЙМЕР МЕТРОВ. `DispatcherTimer` в `MainViewModel` → `IUiTimer`
    (`Interval`/`Ticked`/`Start`/`Stop`/`IDisposable`), реализация
    `Services/DispatcherTimerAdapter.cs`. Период 33 мс остался в ядре (`MeterInterval`),
    регистрация в DI — синглтон (создаётся на UI-потоке в `App.OnStartup`).
  * ФИЛЬТР СПИСКА. `ICollectionView` + `Filter` → обычный `ObservableCollection`
    `FilteredInstalledApps` и `RefilterInstalledApps()` (тот же предикат: имя/издатель,
    регистронезависимо). Вызывается из обработчика строки поиска и после загрузки списка.
  * БУФЕР ОБМЕНА. `Clipboard.SetText` в `LogViewModel` и `ObsDockSettingsViewModel` →
    `IClipboardService`; реализация `Services/ClipboardService.cs` бросает наружу, если
    вызвали не с UI-потока (буфер живёт в STA).
- ГРАНИЦА ЗАКРЕПЛЕНА ТЕСТОМ: `CoreWpfFreeTests` (в Core.Tests) — обход ВСЕГО графа
  ссылок ядра и запрет `PresentationFramework`/`PresentationCore`/`WindowsBase`/
  `System.Xaml`, плюс проверка, что у ядра нет точки входа. В `ModuleBoundaryTests`
  добавлена парная проверка «у приложения точка входа есть, у ядра нет».
  `SoundMeeter.Core.Tests` тоже остался без `UseWPF` — вторая линия защиты (компилятор).
- ТЕСТЫ ЗНАЧКОВ (новых 9, `AppIconTests`): значок по реальному `System32\notepad.exe`
  непустой и замороженный, кэш возвращает тот же экземпляр, на пустом/несуществующем
  пути и на мёртвом PID — null без исключения, `ConvertBack` бросает. Раньше на код
  извлечения значка НИ ОДНОГО теста не было — он работал «на глаз».
- ИТОГ: `dotnet build src\SoundMeeter.slnx` (Debug и Release) — 0 ошибок, 0 своих
  предупреждений; `dotnet test` — 120 зелёных (было 108: +2 границы, +1 точка входа,
  +9 значков). `SoundMeeter.Core.dll` больше не ссылается ни на одну сборку WPF, а в
  выходе `SoundMeeter.Core.Tests` WPF-сборок нет вообще.
- ЖИВОЙ ПРОГОН (приложение на машине пользователя): окно открылось, пресет 6 входов /
  5 шин, док OBS на 17954, трей, тихая проверка обновлений. Док-панель проверена по
  сети: WebSocket-клиент получил 3 снимка подряд по 1799 байт, 8 реальных каналов с
  дБ и именами (`engine=True`) — значит `IUiTimer` тикает и публикует состояние. Команда
  `{"op":"mute"}` из дока прошла круг сокет → диспетчер → VM → модель → снимок
  (`mute=False → True`), состояние возвращено (`True → False`).
- ЧЕСТНО О СТАТУСЕ: кликов мышью по UI после этапа 2 не делал (запуск был фоновым, а
  закрыть его из не-elevated шелла нельзя). Значки проверены тестами на реальных файлах,
  но глазами в окне — нет; окно настроек дока и «копировать диагностику» не жал.
  Приложение, запущенное на проверку, осталось жить (PID 29256) — его надо закрыть из
  трея, сам закрыть не могу.
- ЧТО ОСТАЛОСЬ (уже не про границы): `IAudioEngine` на 25 членов (разбить на
  IRoutingTable/IDeviceCatalog/IRStripTopology/IPresetStore); статики `Loc`/`AppLog`;
  CI; отсутствие фильтра по `ProcessId`-у в списке установленных программ не проверяется
  тестом (нет лёгкой фикстуры MainViewModel — 10 зависимостей в конструкторе).

### Resolved (этот turn) — приложение разделено на SoundMeeter.Core и SoundMeeter.App (SM-A10)
- ЗАДАЧА (от пользователя): «Давай разделим SoundMeeter на SoundMeeter.Core и
  SoundMeeter.App». Ответы пользователя на границы: (1) ядро в итоге должно быть
  БЕЗ WPF, но работать поэтапно; (2) тесты разделить на `SoundMeeter.Core.Tests` и
  `SoundMeeter.App.Tests`; (3) `SoundMeeter.Audio` не трогать, оставить модулем.
- ЧТО СДЕЛАНО (этап 1 — механический переезд, логика не менялась):
  * `SoundMeeter.Core` (новый, net10.0-windows): `ViewModels/**` (20 файлов),
    `Services/**` (кроме LocResources), `AudioPolicy/**`, `Resources/
    system_apps_filter.json`, встроенные `obs-dock.{index.html,dock.css,dock.js}`.
  * `SoundMeeter.App` (новый WinExe): `App.xaml(.cs)`, `AssemblyInfo.cs`,
    `app.manifest`, `Views/**`, `Controls/**`, `Converters/**`,
    `Services/LocResources.cs`, `Resources/Icon.ico`.
  * `InternalsVisibleTo`: в Logger/ChangeLanguage/Update/StartUp добавлен
    `SoundMeeter.Core`; в Audio — оба тестовых проекта вместо `SoundMeeter.Tests`.
    В комментариях csproj записано, что имя сборки приложения — `SoundMeeter`,
    и почему это нельзя менять здесь.
- РЕШЕНИЯ, КОТОРЫЕ СТОИТ ПОМНИТЬ:
  * `AssemblyName` у App остался `SoundMeeter`, а не `SoundMeeter.App`: имя exe —
    контракт релиза (`UpdateApplier.MainExecutableName = "SoundMeeter.exe"`,
    проверка архива и `Start-Process` при перезапуске). Переименование — отдельная
    задача, и начинать её надо с `UpdateApplier`.
  * Пространства имён НЕ менялись (`SoundMeeter.ViewModels`,
    `SoundMeeter.Services`, `SoundMeeter.AudioPolicy` в Core; `SoundMeeter.Views`,
    `SoundMeeter.Controls`, `SoundMeeter.Converters` в App). Поэтому ни один
    `using` и ни одна `clr-namespace` в XAML не правились; `pack://.../SoundMeeter
    ;component/Views/Controls/MixerTheme.xaml` в тестах осталась валидной.
  * `LocResources` ушёл в App, а не остался в Core: он подключает словарь к
    `Application.Resources` и ставит `FrameworkElement.Language` — это WPF по
    определению, и в финальном ядре ему не место.
  * obs-dock вшит в `SoundMeeter.Core.dll`: `ObsDockAssets` читает ресурс через
    `Assembly.GetExecutingAssembly()`, и ресурс обязан лежать в той же сборке, что
    сервер, иначе док отдаст 404 после переезда. Проверено: `obs-dock.index.html`,
    `obs-dock.dock.css`, `obs-dock.dock.js` в манифесте Core.
  * `NAudio` и `YellowDogMan.RRNoise.NET` оставлены и в App: кода на NAudio в
    оболочке больше нет, но portable-публикация (PublishSingleFile) обязана
    включать `rnnoise.dll` и сборки NAudio. Убирать — только вместе с проверкой
    публикации.
  * RID `win-x64` продублирован в Core: библиотека без него не получает нативные
    ассеты пакетов в свой выход.
- ТЕСТЫ РАЗДЕЛЕНЫ:
  * `SoundMeeter.Core.Tests` (9): `AudioEnginePresetTests`, `MidiBindingTests`,
    фикстуры `Strips` + `FakeAudioEngine`. Ссылается только на Core.
  * `SoundMeeter.App.Tests` (44): `FuncButtonTests`, `StripEffectTests`,
    `ParamResetTests`, `ModuleBoundaryTests`, STA-хост `UiHost` + `VisualTree`.
    Фикстуры Core линкуются исходниками (`<Compile Include="..\SoundMeeter.Core.Tests\
    Infrastructure\*.cs" Link="..."/>`) — одна правка на два проекта.
  * Про `ModuleBoundaryTests`: он видит все сборки только из App.Tests (App → Core →
    модули, App → TrayIcon), поэтому переехал туда же.
- НОВЫЕ ПРОВЕРКИ ГРАНИЦ (`ModuleBoundaryTests`, +5):
  `NoModuleDependsOnTheCore`, `CoreDoesNotDependOnTheApplication`,
  `CoreDoesNotDependOnUiModules` (по ВСЕМУ графу ядра, а не по прямым ссылкам),
  `ApplicationDependsOnCore`, `ApplicationIsTheOnlyLayerThatKnowsAboutEveryModule`.
- ГРАБЛЯ (важно для будущих проверок): `Assembly.GetReferencedAssemblies()` показывает
  только сборки, ТИПЫ которых реально используются. Добавленный `ProjectReference`
  без единого `using` в метаданных не появляется — первая проба «добавил ссылку на
  TrayIcon» осталась зелёной, и только настоящее использование (`typeof(ITrayIconService)`)
  уронило `CoreDoesNotDependOnUiModules`. Проверено и откачено.
- ПРОВЕРЕНО: `dotnet build src\SoundMeeter.slnx` — 0 ошибок, 0 предупреждений;
  `dotnet test src\SoundMeeter.slnx` — 108 тестов зелёные (было 103: +5 проверок
  границ; 48 тестов SoundMeeter.Tests разошлись как 9 + 39 + 5). В выходе App:
  `SoundMeeter.exe`/`SoundMeeter.dll` на месте, `rnnoise.dll` едет,
  `Resources/system_apps_filter.json` копируется. Живой запуск новой сборки на
  машине пользователя: процесс поднялся, окно «SoundMeeter» открылось, пресет
  восстановился (6 входов, 5 шин), док OBS поднялся на 127.0.0.1:17954, трей
  создался, тихая проверка обновления отработала; второй экземпляр корректно
  отказал по мьютексу.
- ЧЕСТНО О СТАТУСЕ: окно и док вживую не щёлкал (живой прогон UI после переезда не
  делался — приложение запускалось фоновым процессом, и закрыть его из
  не-elevated шелла нельзя: `CloseMainWindow` уходит в трей, `taskkill` отказал по
  правам). Окно, трей и док живы по логу и по заголовку окна; клики по UI после
  разделения не проверялись.
- ЧТО ОСТАЛОСЬ (SM-A10, этап 2 — отдельными шагами, по порядку):
  1. `IIconProvider`: `BitmapImage`/`ImageSource` в `AppViewModel`,
     `ConfiguredAppViewModel`, `InstalledAppViewModel` и `IAudioService.GetAppIcon`
     — это единственное, что тянет WPF в ядро из данных;
  2. `IUiTimer` (или расширить `IDispatcherService`) вместо `DispatcherTimer` в
     `MainViewModel` и `Application.Current.Dispatcher` в `LocalizedViewModel`,
     `MainViewModel.Midi`, `MidiBindingsViewModel`;
  3. отказ от `ICollectionView` в `MainViewModel.Routing` в пользу обычной
     отфильтрованной коллекции (в разметке это `ItemsControl.ItemsSource`);
  4. снять `UseWPF` с Core и закрепить границу тестом по образцу
     `AudioModuleBoundaryTests.CoreDoesNotDependOnWpf` — до этого пункта проверять
     нечего, и врать проверкам нельзя;
  5. после п.4 убрать `UseWPF` и STA-хост из `SoundMeeter.Core.Tests`.

### Resolved (этот turn) — перенос приложения только перенаправляет звук, стрипы не трогаем
- ЖАЛОБА пользователя: «перестало перемещать приложения на стрипы с ошибкой (Не
  удалось закрепить стрип за устройством) и указывается устройство на котором
  приложение находилось до того как его попытались переместить. Мне не нужно чтобы
  приложение держал другой стрип, если я сам выбираю куда его направить».
- ПРИЧИНА (найдена, это была регрессия прошлого turn): приложение играло в
  `CABLE-x Output`; при переносе на стрип-микрофон VM вызывал `SetInputSource` с этим
  устройством, а я в прошлом turn запретил выход кабеля назначать входом (требование
  «в Inputs только входы») → движок молча отказывал, и VM показывал ложную ошибку
  «не удалось закрепить». То же самое делал старый блок «устройство снимает только
  один стрип»: он перенаправлял приложение в КАНАЛ ДРУГОГО стрипа вместо
  выбранного пользователем.
- УТОЧНЕНИЕ пользователя (меняет модель): «нужно перенаправить звук приложения на
  другой стрип, а стрипы не трогать, их привязка друг к другу не меняется (если
  приложение играет в Cable-x Output, который снят стрипом “MIC CABLE-x Input”, а мы
  указываем переместить на “MIC CABLE-y Input”, то перемещаем приложение на связанный
  с ним выход Cable-y Output, а в связанных приложениях с “MIC CABLE-y Input”
  отображаем все приложения, связанные с Cable-y Output)». Плюс: при отсутствии у
  стрипа render-источника — сразу отказ с объяснением, а не попытка конвертации.
- ИТОГ: правило стало одно и простое — цель переноса задаёт ВЫБРАННЫЙ стрип
  (`AppSourceDeviceId`), и больше не меняется ничего: ни источник стрипа, ни
  привязки соседей, ни маршруты по шинам. Пример пользователя работает буквально так,
  как он описал, а список приложений стрипа и так уже отбирается по `AppSourceDeviceId`
  (`RefreshStripApps`).
- ИЗМЕНЕНИЯ:
  * `ViewModels/MainViewModel.Routing.cs` — из `AssignAppToStripAsync` удалён весь
    блок конвертации стрипа (и «владелец устройства», и перевод в loopback с
    гашением маршрута-петли); осталось `AppSourceDeviceId is not { } → отказ`.
    Удалены осиротевшие `ResolveAppCarrierDevice`, `IsRenderDevice`, `StripTitle`.
  * `ViewModels/InputChannelViewModel.cs` — `CanAcceptApps` теперь честный
    (`AppSourceDeviceId != null`): микрофон и неназначенный вход больше не
    подсвечиваются как drop-цель; `AppRejectReason()` даёт текст объяснения;
    `AppDropHint` для них: «Микрофон приложений не принимает» / «Источник не
    назначен». Мёртвый `HasAppSource` удалён.
  * `Views/Controls/InputStripView.xaml.cs` — drop на стрип без render-источника
    больше не игнорируется молча, а показывает `AppRejectReason()`.
- ЧТО ОСТАЛОСЬ ПРЕЖНИМ: `SetInputSource` по-прежнему не берёт выход кабеля (это путь
  ручного выбора источника в пикере — единственный вызывающий); авто-удаление
  дублей `SPK CABLE-x Output` из прошлого turn тоже на месте — оно не меняет привязки
  стрипов, а убирает стрип, который больше не нужен. Если это тоже не нужно — скажи.
- ПРОВЕРЕНО: сборка модуля без ошибок. Живой прогон переноса на машине пользователя
  не делался — глянуть стоит: перенос приложения, играющего в CABLE-x Output, на
  «MIC CABLE-y Input» должен перенаправить его в CABLE-y Output и показать его в
  списке приложений этого стрипа.

### Resolved (предыдущий turn) — в Inputs остались только входы: связка половинок виртуального кабеля
- ЗАДАЧА (от пользователя): «в Inputs уберем каналы которые SPK и помечены как Virtual
  Audio Cable, но роутинг будет идти на них. То есть сразу сделаем связку, если у нас
  есть Virtual Audio Cable вход и выход, то мы их связываем, в Input можем добавить
  только входы, а роутинг приложений будет перенаправлен на связанный выход».
  Ответы пользователя: (1) авто-перевод стрипа-микрофона в loopback при переносе
  приложения — ОСТАВИТЬ как было; (2) уже существующие «SPK CABLE-x Output» —
  УДАЛЯТЬ автоматически.
- ТОПОЛОГИЯ (почему так): у виртуального кабеля всегда два endpoint'а — «CABLE-x
  Input» (захват) и «CABLE-x Output» (воспроизведение). Звук приложений, ушедший в
  Output, приходит в Input. Значит половинки надо связать, и дальше схема одна:
  приложение → CABLE-x Output (куда играют) → кабель → CABLE-x Input (это и есть
  входной стрип микшера) → OUT/VIRT → реальные выходы. Стрип «SPK CABLE-x Output»
  (loopback того же выхода) в этой схеме — второй канал того же звука, а маршрут
  «выход кабеля → выход кабеля» к тому же замыкает петлю обратной связи.
- НОВЫЙ ФАЙЛ `Services/CablePairing.cs`: эвристика «виртуальный кабель» по имени
  (cable/vb-audio/virtual — та же, что работала в OUT/VIRT) + связывание половинок.
  Ключ пары = имя без слова Input/Output (оно бывает и в конце имени, и перед
  «(VB-Audio Virtual Cable)»); сначала строгий ключ (важно: у разных кабелей
  различается буква, CABLE-A ≠ CABLE-B), затем loose-ключ без скобок — на случай,
  когда драйвер дописал название только с одной стороны. `GetAppRenderDeviceId()` —
  единственное место в коде, где решается «куда играть приложению стрипа».
- ИЗМЕНЕНИЯ:
  * `Services/IAudioEngine.cs` — `DeviceInfo` получил `IsVirtualCable` + `CablePeerId`.
  * `Services/WasapiAudioEngine.cs` — каталог строится через `CablePairing.Link`;
    новый `DropRedundantCableLoopbacksUnlocked()` снимает лишние SPK-кабели, но
    только если половинка со стороны захвата уже снята своим стрипом (если
    пользователь скрыл вход кабеля, его loopback остаётся — не разрушаем ручную
    настройку); `SetInputSource()` отказывает на выходе кабеля, т.е. в Inputs
    остаются только входы; `DetachInputUnlocked()` вынесен из `SetInputSource`.
  * `ViewModels/InputChannelViewModel.cs` — `AppSourceDeviceId` (реальная цель
    переноса приложений) + `IsCableInput`; `HasAppSource` и `AppDropHint` считаются
    через них; ctor принимает каталог.
  * `ViewModels/MainViewModel.Routing.cs` — `AssignAppToStripAsync` роутит в
    `AppSourceDeviceId` и НЕ переводит стрип входа кабеля в loopback (раньше перенос
    приложения на такой стрип ломал его источник); `RefreshStripApps` сверяется с
    render-устройством, а не с `DeviceId` стрипа (у кабеля это разные endpoint'ы).
  * `Views/DevicePickerWindow.xaml.cs` — выходы кабеля не предлагаются как «SPK
    … (loopback)».
  * `ViewModels/LogViewModel.cs` — в отчёт копирования добавлены `cable-peer=` и
    `apps->` (отвечают на «почему приложение не роутится»).
  * `ViewModels/MainViewModel.cs` — `HasVirtualCable` по каталогу (учитывает и вход
    кабеля, а не только шины).
- ПРОВЕРЕНО: сборка всех C#-проектов решения без ошибок (C++-драйвер в `dotnet
  build` не собирается — нужен MSBuild из VS, это не связано с правкой). Офлайн-
  харнесс, компилирующий НАСТОЯЩИЙ `CablePairing.cs` (не копию логики), на именах
  endpoint'ов: VB-Cable A и B, VAC («VoiceMeeter Input/Output (CABLE-A/B)»),
  StreamerCable «CABLE-1», «Cable Input»/«CABLE Output», скобки только с одной
  стороны, выход без входа — 13 проверок, A/B не смешиваются, лишние половины
  остаются без пары.
- ЧЕСТНО О СТАТУСЕ: живой прогон с настоящим VB-Cable/VAC на машине пользователя НЕ
  делался (в этом turn харнесс проверял только разбор имён). Что стоит глянуть
  руками: (1) после «Refresh devices» пропал ли «SPK CABLE-x Output», а приложения
  из его списка не пропали из канала и слышны; (2) перенос приложения на стрип
  «MIC CABLE-x Input» не переписывает его источник и роутит в «CABLE-x Output».
- ОГРАНИЧЕНИЕ (осознанное): связывание половинок — по имени, потому что Windows не
  отдаёт «свой» endpoint у пары. У кабеля, у которого половинки названы нестандартно
  (нет слов cable/vb-audio/virtual), пара не найдётся — поведение будет как раньше
  (loopback и перенос приложения переводят стрип на выход приложения).

### Resolved (предыдущий turn) — кнопки тулбара под одну «Settings»
- ЗАДАЧА (от пользователя): «Перемести кнопки "Refresh Devices", "Midi", "OBS",
  "Updates", "Logs" под одну кнопку настроек» (пункт был в `Obsidian/…/Tasks.md`).
- РЕШЕНИЕ: одна кнопка `Settings` с иконкой Cog (`wpf:Button.LeftIcon`) и выпадающий
  `Popup` (`StaysOpen="False"`, та же тёмная подложка, что у попапов OUT/VIRT на
  стрипах). В меню 5 пунктов с иконками (Refresh / Midi / Monitor / CloudDownload /
  Text) и прежними тултипами: Refresh devices (по-прежнему на `RefreshCommand`),
  MIDI, OBS dock, Check for updates, Logs. Start/Stop и оба текста статуса остались
  на своих местах.
- НОВЫЙ СТИЛЬ `MenuRow` в `MixerTheme.xaml` (строка меню: текст слева, подсветка при
  наведении) — чтобы не плодить копии шаблона кнопки.
- ДЕТАЛЬ, О КОТОРОЙ СТОИТ ЗНАТЬ: у открытого `Popup` захвачена мышь, поэтому повторный
  клик по кнопке не доходит до `Click` — закрывает его сам Popup по клику снаружи.
  Каждый обработчик пункта дополнительно закрывает меню первой строкой (иначе
  модальное окно MIDI открылось бы поверх оставшегося попапа). Инверсия состояния
  кнопки отдельным флагом НЕ заводилась — как и у соседних попапов стрипов.
- ПРОВЕРЕНО: сборка модуля без ошибок. Живой клик по меню не прогонялся.

### Resolved (предыдущий turn) — док-панель SoundMeeter в OBS
- ЗАДАЧА (от пользователя): «если ПО открыто, то можно добавить его как док панель
  в OBS. Всё что нужно в этой док панели: VU-meter, ползунок громкости, кнопки
  (Mute, Solo). Так же в SoundMeeter добавить настройки, в которых можно указать
  какие каналы будут отображаться в док-панели в OBS». Ответы пользователя:
  (1) способ — встроенный HTTP+WebSocket сервер + Custom/Browser Dock OBS
  (НЕ C++-плагин); (2) автоустановка — да, кнопкой «Install dock in OBS».
- ИДЕЯ: OBS открывает док как обычную веб-страницу (Browser/CEF), поэтому SoundMeeter
  сам поднимает локальный сервер и отдаёт панель. Никаких плагинов, сборок OBS SDK и
  файлов на диске: страница лежит во встроенных ресурсах сборки, панель всегда
  соответствует версии приложения (важно и после автообновления portable-сборки).
  Адрес: `http://127.0.0.1:17954/` → OBS: View → Docks → SoundMeeter.
- НАХОДКА ПРО УСТАНОВКУ (решающая): OBS 32.2.2 хранит «лишние» браузерные доки
  (у пользователя там уже есть TTS-док) в `%APPDATA%\obs-studio\user.ini`, секция
  `[BasicWindow]`, ключ `ExtraBrowserDocks=[{"title":…,"url":…,"uuid":…}]`. Это и
  есть машиночитаемый список доков, который OBS читает на старте. `DockState`
  (base64-блоб QMainWindow с геометрией) НЕ трогаем — его OBS перезапишет сам.
- НОВЫЕ ФАЙЛЫ:
  * `Models/ObsDockSettings.cs` — Enabled/Port/ShowAllInputs/ShowAllOutputs/Channels
    (`ObsDockChannelRef`: StripId + DeviceId + kind — DeviceId нужен потому, что
    движок пересоздаёт стрип с НОВЫМ Id при повторном обнаружении устройств, и по
    одному Id выбранный канал молча исчез бы из дока после перезапуска).
  * `Models/ObsDockMessages.cs` — `ObsDockState`/`ObsDockChannelState` (протокол),
    `ObsDockCommand.TryParse` (JsonDocument, без рефлексии), константы ops/kind.
  * `Services/IObsDockServer.cs` + `Services/ObsDockServer.cs` — сервер на TcpListener
    со СВОИМ мини-HTTP и WebSocket: 101-рукопожатие, кадры (маска клиента, сервер без
    маски, чанкинг >64 КиБ, ping/pong, close), маршруты `/`, `/dock.css`, `/dock.js`,
    `/api/state` (запасной опрос 100 мс, если WebSocket недоступен), `/favicon.ico`,
    404 на всё остальное, limit 4 КиБ на голову запроса. Сериализация состояния
    ручная (`ObsDockJson`) — 30 Гц без аллокаций на кадр ленты; очередь на клиента
    ограничена 4 кадрами с DropOldest (свежие метры важнее истории). Только loopback.
  * `Services/ObsDockAssets.cs` — выдача index.html/dock.css/dock.js из
    EmbeddedResource (`LogicalName="obs-dock.*"`), кэш байт[].
  * `Services/ObsDockInstaller.cs` — `ObsDockInstaller` (путь %APPDATA%\obs-studio,
    `IsObsRunning`, гвард «OBS закрыт») + `ObsDockConfig` (правка INI без знания о
    процессах — так её можно проверять на копиях файла) + `IniValueReader`
    (замена строки по секции, сохранение CRLF/порядка, вставка секции при нужде,
    бэкап `user.ini.soundmeeter.bak`, upsert по постоянному uuid
    `5c0f3a91b7d24e6aa1c0b8e2d4f61a37` — повторная установка обновляет URL, а не
    плодит копии; битый JSON → отказ, файл не трогаем).
  * `Resources/obs-dock/{index.html,dock.css,dock.js}` — панель: на канал карточка
    118px (бейдж IN/OUT, имя, canvas-метр 26 сегментов с той же формулой шкалы, что в
    SegmentedMeter.cs, шкала −60…+6 дБ, peak-hold белым), dB, фейдер −60…+12 (двойной
    клик = 0 дБ, пока тянут — серверное значение не затирает), кнопки MUTE/SOLO;
    тёмная тема под OBS, индикатор связи, авто-реконнект WS.
  * `ViewModels/MainViewModel.ObsDock.cs` — сбор снимка `BuildDockState` (ShowAllInputs/
    ShowAllOutputs + явный список по Id ИЛИ DeviceId), публикация из UI-таймера метров
    (только если есть подключённые панели), применение команд в UI-потоке через
    IDispatcherService, Start/Stop/ToggleServer, Install/Remove из OBS.
  * `ViewModels/ObsDockSettingsViewModel.cs` + `ObsDockChannelItemViewModel` — окно
    настроек: вкл/выкл сервер, порт, Start/Stop, Copy URL, Install/Remove in OBS,
    «All input/output channels» + списки каналов (список блокируется, когда включено
    «все»), Apply/Close, статус и ошибки.
  * `Views/ObsDockSettingsWindow.xaml(.cs)` — тёмное окно, стили из MixerTheme.
- ИЗМЕНЕНО: `App.xaml.cs` (DI `IObsDockServer`→`ObsDockServer`), `MainViewModel.cs`
  (ctor +`IObsDockServer`, `UpdateMeters` → `PublishDockState()`, `Shutdown` →
  `_dock.Stop()`), `MainViewModel.Persistence.cs` (`Restore`: подъём дока после
  восстановления стрипов), `AppSettings.ObsDock`, `SettingsService.SaveSync` +
  `CloneObsDock` (снимок движка про док не знает — переносим из Settings, как
  PersistentRoutes), `MixerToolbarView` (кнопка «OBS» + OnObsDockClick),
  `SoundMeeter.csproj` (3 EmbeddedResource).
- СЕТЕВОЙ ПРОТОКОЛ: состояние `{eng,min,max,ch:[{id,k,n,d,dev,db,p,m,s,o,a}]}` на
  30 Гц; команда `{op,id,kind,v}` (vol/mute/solo/mono). Снимок целиком, а не дельты:
  пакет единицы килобайт, зато док сам восстанавливается после потери пакета.
- ПРОВЕРЕНО ЖИВЫМ ПРОГОНОМ (приложение запускалось на машине пользователя):
  старт сервера на 17954, снимок с 5 реальными стрипами (Микрофон/Discord/OBS/Music/
  Browser, dB и имена верные), команда mute из «дока» реально переключила стрип
  (False→True в ответе приложения) — то есть замкнут весь круг сокет→UI-поток→VM→
  модель→снимок обратно. Страница отдаётся и РИСУЕТСЯ: headless Edge (153) с
  настоящим dock.js — 4 карточки, кириллица, dB, флаги mute/solo, offline-класс,
  бейджи IN/OUT, а по пикселям canvas: p=1.0 → 24 сегмента, p=0.5 → 22, p=0.05 → 14,
  p=0 → 0; верхний сегмент оранжево-красный с peak-hold, нижний зелёный #22B14C —
  совпадает с формулой SegmentedMeter.
- ХАРНЕСС `Temp\opencode\obs-dock-harness` (ссылка на проект SoundMeeter, 82
  проверки, ВСЁ ЗЕЛЁНОЕ): HTTP-маршруты (404, favicon, query, обход каталога), WS
  рукопожание (Sec-WebSocket-Accept сверен с RFC), push снимка (кириллица, флаги,
  дБ), команды vol/mute + игнор мусора, чанкинг >64 КиБ (400 каналов склеились),
  ping→pong, close снимает клиента, /api/state без WebSocket, Stop освобождает порт,
  занятый порт → IOException с подсказкой, разбор команд (10 кейсов), правка user.ini
  на копии НАСТОЯЩЕГО файла (чужой TTS-док сохранён, остальной конфиг байт в байт,
  повторная установка без дублей, uninstall, битый JSON не трогает файл, нет
  секции/пустой файл) и гвард «при запущенном OBS — отказ».
- БАГ, НАЙДЕННЫЙ ХАРНЕССОМ (компилятор бы не увидел): `DockClient.Enqueue` клал в
  очередь СЫРОЙ JSON без заголовка кадра WebSocket. Панель получала мусор и не
  работала бы. Очередь должна хранить готовые кадры: `WebSocketFrame.Build(Text, …)`.
- ГРАБЛИ (пригодятся дальше):
  * `HttpListener` не умеет апгрейд в WebSocket, а Kestrel/AspNetCore ради трёх
    файлов и одного сокета затащил бы в portable-сборку лишний фреймворк ⇒ свой
    мини-HTTP поверх TcpListener.
  * Edge в этом окружении не пишет скриншоты (`Failed to write file: Access denied`)
    и режет http-подресурсы со страницы file:// как mixed content ⇒ страницу для
    проверки надо отдавать по http (в харнессе для этого есть режим `serve`).
  * PowerShell 5.1 `Get-Content`/`Set-Content` без -Encoding ломает UTF-8 в .cs
    (русские строки превращаются в мусор, и потом не совпадают с теми, что в коде) —
    не править исходники такими командами.
  * Edge/х��рнесс-проверки правят НИКАКИХ файлов вне Temp; settings.json пользователя
    на время прогона подменялся и восстановлен побайтово (SHA256 совпал).
- ЧЕСТНО О СТАТУСЕ: окно настроек дока и сам док внутри OBS живым прогоном НЕ
  проверены — для этого нужен перезапуск OBS (у пользователя на момент конца работы
  OBS запущен, закрывать его было нельзя). Проверены: сборка, логика сервера и
  установщика (харнесс), отрисовка панели настоящим dock.js в браузере и полный
  круг команд до стрипов на запущенном приложении.
- ОГРАНИЧЕНИЕ (осознанное): если порт дока сменить, старая запись в OBS останется со
  старым URL — её обновит та же кнопка «Install dock in OBS» (upsert по uuid).
  Состояние окна/размер дока OBS хранит у себя (DockState), мы их не задаём.

### Resolved (предыдущий turn) — автообновление SoundMeeter через GitHub Releases
- ЗАДАЧА (от пользователя): «напиши проверку обновления через GitHub Releases и
  обновления через него. Для SoundMeeter». Ответы пользователя: (1) репозиторий —
  `IDerkBot/SoundMeeter` (взят из git remote); (2) способ — portable ZIP: скачать,
  распаковать, заменить файлы, перезапустить; (3) когда — тихо при старте + кнопка
  «Check for updates» в тулбаре; (4) показывать диалог подтверждения с прогрессом.
- НОВЫЕ ФАЙЛЫ:
  * `Models/AppVersion.cs` — semver-версия: `TryParse` (терпит `v1.2.3`,
    `release-1.4.7`, `1.2.3+commit`, `1.2.3.9`), сравнение major/minor/patch +
    «релиз новее пре-релиза», операторы ==/!=/</>/<=/>=, `AppVersion.Current`
    из `AssemblyInformationalVersion`.
  * `Models/GitHubReleaseDto.cs` — DTO ответа `releases/latest` (internal, snake_case
    через JsonPropertyName).
  * `Models/UpdateInfo.cs` — `UpdateAsset` (+HumanSize), `UpdateInfo`
    (Version/TagName/ReleaseNotes/HtmlUrl/Asset/CanInstall), `UpdateCheckResult`.
  * `Services/IUpdateService.cs` + `Services/UpdateService.cs` — GET
    `api.github.com/repos/IDerkBot/SoundMeeter/releases/latest` (User-Agent +
    Accept обязательны), выбор ассета (win-x64 > любой .zip, отбрасывая
    symbols/source/debug), скачивание с `IProgress<double>`, распаковка
    (`ZipFile` + спуск в единственную корневую папку публикации), `ApplyAndRestart`,
    `OpenReleasePage`. HttpClient.Timeout поднят до 10 мин (таймер живёт до конца
    чтения тела даже при ResponseHeadersRead).
  * `Services/UpdateApplier.cs` — подмена файлов фоновым powershell-скриптом:
    ждёт exit по PID → `robocopy /MIR` (20 попыток, коды <8) → чистит payload и
    себя → `Start-Process` exe. Проверка «в архиве есть SoundMeeter.exe» и
    «каталог установки доступен для записи» ДО запуска скрипта.
  * `ViewModels/UpdateViewModel.cs` — диалог: версии, changelog, прогресс-бар,
    Install/Закрыть/Отмена/Открыть в GitHub, событие `InstallCompleted`.
  * `Views/UpdateWindow.xaml(.cs)` — тёмное модальное окно.
  * `ViewModels/MainViewModel.Update.cs` — partial: `CheckUpdatesOnStartupAsync`,
    `CheckUpdatesAsync`, `DismissUpdateCommand`, `PendingUpdate`, баннер.
- ИЗМЕНЕНО: `App.xaml.cs` (DI `IUpdateService` + тихая проверка после `Show()`),
  `MainViewModel.cs` (ctor +`IUpdateService updateService`), `MainView.xaml`
  (кнопка «Updates», зелёный баннер с кнопкой Update и ✕, `UpdateStatus` в тулбаре;
  Row=1 стал StackPanel, где теперь два баннера), `MainView.xaml.cs`
  (`OnCheckUpdatesClick`, `OnUpdateInstallClick`, `ShowUpdateWindow`),
  `SoundMeeter.csproj` (`<Version>1.0.0</Version>`), `app.manifest`
  (`assemblyIdentity version="$(AssemblyVersion)"`).
- ДВА БАГА, НАЙДЕННЫЕ ХАРНЕССОМ (не компилятором):
  1. `1.2.3+abc1234` и `1.2.3.9` разбирались как `1.2.3-abc1234`/`1.2.3-9`, т.е.
     релиз считался ПРЕ-релизом. Фикс: суффикс берётся только если остаток
     начинается с `-` (semver), `+build`/лишняя компонента отбрасываются.
  2. Скрипт записывался в .ps1 как UTF-8 **без BOM** → Windows PowerShell 5.1 читал
     его как ANSI, кириллица в комментариях ломала ПАРСЕР (весь апдейт не работал).
     Фикс: `new UTF8Encoding(true)`. ВАЖНО для любых будущих .ps1-генераторов.
- ХАРНЕСС `Temp\opencode\update-harness` (вне репозитория, ссылка на проект
  SoundMeeter.csproj, internal-доступ через reflection): 42 проверки —
  AppVersion (парс/сравнение), SelectAsset, FindPayloadRoot, Extract,
  DownloadAsync через локальный HttpListener (3 МБ, прогресс), парсинг .ps1
  парсером PowerShell, РЕАЛЬНЫЙ прогон скрипта (robocopy /MIR заменил файлы,
  добавил новый, удалил stale, почистил payload и себя), отклонение архива без
  SoundMeeter.exe. ВСЁ ЗЕЛЁНОЕ (HARNESS OK).
- СОСТОЯНИЕ РЕЛИЗОВ: репозиторий `IDerkBot/SoundMeeter` существует и публичный,
  но релизов в нём НЕТ (API отдаёт 404) → 404 обработан как «релизов пока нет»,
  а не как ошибка. Живой `CheckAsync` на машине: `1.0.0 — релизов пока нет`.
- ЧТО НУЖНО ДЛЯ РЕЛИЗА: поднять `<Version>` в csproj, собрать portable-публикацию
  и приложить к релизу zip (имя с `win-x64` приоритетнее), тег `v1.1.0`.
  Архив должен содержать `SoundMeeter.exe` в корне (или в одной корневой папке).
- ОГРАНИЧЕНИЕ (осознанное): `robocopy /MIR` удаляет из каталога установки всё, чего
  нет в архиве. Настройки лежат в `%APPDATA%\SoundMeeter`, поэтому не страдают,
  но portable-данные рядом с exe будут удалены.
- ЧЕСТНО О СТАТУСЕ UI: окно обновления и баннер живым прогоном на машине НЕ
  проверены (нужен реальный релиз); проверены только сборка/XAML-биндинги и логика
  сервисов харнессом. Запуск приложения после правок проверен: процесс живёт,
  окно «SoundMeeter» открывается.
- ГРАБЛЯ (сломала запуск, поймал только запуск exe, не компилятор):
  `app.manifest` вшивается SDK КАК ЕСТЬ — MSBuild-свойства в нём НЕ раскрываются.
  Я подставил `version="$(AssemblyVersion)"`, и Windows отказалась стартовать:
  «Не удалось запустить программу — неверная конфигурация, просмотрите манифест».
  Версия в манифесте должна быть литералом; расхождение с AssemblyVersion
  безвредно (SxS не используется). Проверять: искать в байтах exe строку `$(`.

### Resolved (предыдущий turn) — драйвер виртуальных кабелей StreamerCable
- ЗАДАЧА (от пользователя): «напиши драйвер для создания виртуальных кабелей
  как VoiceMeeter». Ответы пользователя: (1) WDM KS-драйвер в ядре (как
  VB-Cable); (2) N экземпляров при установке; (3) из сопутствующего — только
  установщик/мастер установки (C# interop и UI в SoundMeeter — НЕ делать).
- ЧТО СДЕЛАНО: новая папка `Driver/StreamerCable/` — KMDF-драйвер
  (PortCls + WaveRT, минипорт-топология). Один root-enumerated девnode = один
  кабель = пара endpoint'ов Windows: `CABLE-x Output` (playback) и
  `CABLE-x Input` (recording). Звук, отрендеренный в Output, выходит из Input
  через общий кольцевой буфер в адаптере.
- БАЗА: форк `VirtualDrivers/Virtual-Audio-Driver` (MIT) — это урезанный под
  нужды WDK-семпл `audio/simpleaudiosample` (Microsoft, WDK-лицензия).
  Атрибуция в `Driver/StreamerCable/THIRD_PARTY_NOTICES.md`. Переименование
  VirtualAudioDriver→StreamerCable и Speaker/MicArray→CableOut/CableIn
  (аккуратно, с восстановлением KSAUDIO_*/KSMICARRAY_*/KSNODETYPE_SPEAKER).
- КАБЕЛЬ (главное самописное):
  * `Source/Inc/cablebuffer.h` + `Source/Main/cablebuffer.cpp` — `CCableBuffer`:
    кольцо 2^k, non-paged, один KSPIN_LOCK, Write дропает старое при
    переполнении, Read зануляет недостающее (underrun = тишина), Reset(),
    CABLE_STATS.
  * Точка врезки — `CMiniportWaveRTStream::WriteBytes/ReadBytes`
    (minwavertstream.cpp): render сливает движоковский DMA-буфер в кольцо,
    capture наполняет свой DMA-буфер из кольца. Никаких DPC/таймеров не надо:
    обе стороны тянет аудиодвижок. ВАЖНО: в исходном семпле ReadBytes вызывался
    только при включённом дампе .wav — это пришлось убрать, иначе кабель пуст.
  * Сброс кольца: на KSSTATE_STOP рендер-потока и на KSSTATE_RUN захват-потока
    (чтобы не отдавать «хвост» старого аудио).
  * Формат провода один на оба конца: 48 кГц / 16 бит / 2 канала
    (`CABLE_WIRE_*` в definitions.h). `DataRangeIntersection` в minwavert.cpp
    принудительно отдаёт этот формат обоим endpoint'ам, иначе движок мог бы
    открыть render в 24 битах и в кольце был бы мусор.
  * Кольцо принадлежит адаптеру: `IAdapterCommon::GetCableBuffer()` (добавлен
    в интерфейс), создаётся в `CAdapterCommon::Init`, освобождается в
    `Cleanup` и в деструкторе.
- INSTALLER (`Driver/StreamerCable/Installer/`, PowerShell):
  Install / Uninstall / Test + StreamerCable.Common.ps1. Installer: msbuild →
  тест-сертификат CN=StreamerTools Test Cert (CurrentUser\My + LocalMachine
  Root/TrustedPublisher) → staging → перегенерация INF (сегодняшний DriverVer
  и только нужные модели, между метками `;@@MODELS@@`) → inf2cat → signtool →
  реестр Parameters (CableRingMs/DoNotCreateDataFiles) → `pnputil
  /add-driver /install` → по devnode на кабель через
  `newdev!DiInstallDevice` (Scope=SPAPI_SCOPE_DEVICES) с HWID
  `ROOT\STREAMERCABLE_A` … `_H` → вывод найденных CABLE-* endpoint'ов из
  MMDevices. Test-StreamerCable.ps1 играет 440 Гц через MixerOutputStream в
  Output и меряет пик из WasapiCapture на Input (NAudio из SoundMeeter).
- ТЕСТЫ: `Driver/StreamerCable/Tests/run-cablebuffer-tests.ps1` компилирует
  НАСТОЯЩИЙ cablebuffer.cpp в user-mode против заглушек ntddk.h/definitions.h
  (Tests/user-mode) и гоняет 29 проверок (размер, round trip, underrun,
  overrun, wrap-around 200 итераций, Reset, статистика) — ВСЕ ЗЕЛЁНЫЕ.
- ЧЕСТНО О СТАТУСЕ: драйвер НЕ собран и НЕ загружен. На машине только
  user-mode часть WDK (нет `km`-заголовков и portcls.lib) и нет прав админа.
  В README это написано явно. Скрипты установщика проверены только на
  синтаксис + логика генерации INF (New-StagedInf прогнан отдельно).

### Resolved (предыдущий turn) — перенос приложения из RunningApps на Strip
- ЗАДАЧА: перетащить приложение из панели «Приложения с аудио» (RunningApps) на
  входной стрип и получить Route этого приложения на канал стрипа.
- КОРЕНЬ ПРОБЛЕМЫ (найден): `AdoptDevicesUnlocked` (WasapiAudioEngine.cs) создавал
  input-стрипы ТОЛЬКО из микрофонов, а render-устройства становились шинами. Поэтому
  `InputChannelViewModel.CanAcceptApps` был false у всех стрипов и дроп отклонялся
  (не было ни одного стрипа-приёмника приложений). С 0.1.2 автосоздание стрипов
  убрано целиком, но выводы остаются: render-устройство пользовательский стрип
  цепляет сам.
- РЕШЕНИЕ ПО ВЫБОРУ ПОЛЬЗОВАТЕЛЯ: дроп на ЛЮБОЙ input-стрип; если стрип не снимает
  render-устройство — он автоматически цепляется к текущему выходу приложения через
  loopback (звук слышно не меняется, стрип получает канал приложения). Маршруты
  OUT/VIRT стрипа НЕ трогаем.
- `InputChannelViewModel`: CanAcceptApps — любой стрип; новое HasAppSource
  (= !IsMicrophone && IsAvailable && DeviceId != ""); новый AppDropHint.
- `MainViewModel.Routing.AssignAppToStripAsync` переписан:
  * цель = DeviceId стрипа, если это HasAppSource; иначе carrier = ResolveAppCarrierDevice(app)
    (текущий выход приложения → его правило → render-устройство по умолчанию);
  * `ResolveAppCarrierDevice` + `IsRenderDevice`/`DeviceName`/`StripTitle`/`DeviceEquals`;
  * стрип вешается на carrier через `_engine.SetInputSource` (wasapi-loopback), с проверкой
    результата; stripId запоминается ДО, т.к. SetInputSource → ChannelsChanged → пересборка VMs;
  * ЗАЩИТА ОТ ПЕТЛИ: если маршрут нового источника в одноимённую шину был включён — выключается;
  * если carrier уже снимает другой стрип — не ошибка, а честное сообщение: приложение
    физически в канале того стрипа (дублировать loopback нельзя, движок даёт 1:1);
  * правило пишется ДО обращения к Windows (как было), Status расширен подсказкой про OUT/VIRT.
- `ConfiguredAppViewModel` получил `DeviceId` (перенос pending-приложений между стрипами).
- `RefreshStripApps`/`RefreshConfiguredAppsInUI` прокидывают DeviceId в ConfiguredAppViewModel.
- Проверено: сборка SoundMeeter без ошибок и без новых предупреждений; харнесс
  (Temp\opencode\strip-route-harness, реальные устройства: 19 mic / 25 render) —
  микрофонный стрип успешно переводится на loopback render-устройства (IsMicrophone снят,
  имя становится «SPK …»), второй стрип не может дублировать устройство (1:1), возврат на
  микрофон работает, неизвестное устройство отклоняется. HARNESS OK.
- Известное ограничение (осознанное): если два разных приложения играют в одно и то же
  render-устройство, они попадают в канал одного стрипа — развести их без виртуального
  кабеля (VB-Cable/VAC) технически нельзя. Ровно эту задачу и решает
  `Driver/StreamerCable`: приложения получают РАЗНЫЕ render-устройства
  (`CABLE-x Output`) и потому разные микрофоны (`CABLE-x Input`).

### Resolved (предыдущий turn)
- КАРТИНА ИЗМЕНИЛАСЬ: по решению пользователя микшер переведён с матрицы на
  VoiceMeeter-подобные ВЕРТИКАЛЬНЫЕ СТРИПЫ (input strips слева, output strips справа),
  без матрицы вообще. У каждого стрипа: вертикальный фейдер громкости, вертикальный
  VU-метр, подпись dB, кнопки M/S/MUTE. У входного стрипа — две кнопки вывода
  OUT ▾ (аппаратные, «для прослушивания») и VIRT ▾ (VB-Cable/VAC), каждая открывает
  PoпPOP с чекбоксами (мульти-выбор, live-применение). Ответы пользователя:
  (1) входы+выходы; (2) можно несколько выходов сразу; (3) матрицу убрать совсем;
  (4) A = для прослушивания, B = виртуальные; (5) Mono/Solo/Mute — все три сразу.
  Изображение из запроса прочитать НЕ удалось — модель не принимает image-вход
  (сообщил пользователю до старта).
- АРХИТЕКТУРА: маршрут-гейн (BusRouting.GainDb) больше НЕ применяется в аудио-пути
  (BusTap теперь умножает только _input.VolumeDb + mute + solo-правило). Громкость
  стрипа — единственный источник громкости. GainDb остался в модели/пресете только
  как безвредное поле. SetRouteGain удалён из IAudioEngine и из движка.
- SOLO/MONO:
  * Новый `Audio/SoloState.cs` (volatile AnyInputSolo/AnyOutputSolo), создаётся движком,
    пробрасывается в BusTap и BusDsp — без статики.
  * Движок: `SetInputSolo(id,val)` / `SetOutputSolo(id,val)` — ставят флаг в модели и
    пересчитывают _soloState под lock. Mono — обычное поле модели, читается напрямую.
  * BusTap: `volume = muted?0:DbToLinear(_input.VolumeDb)`, где muted = _input.IsMuted
    || (AnyInputSolo && !_input.IsSolo); убран _bus и per-route gain.
  * BusDsp: solo-правило по _bus.IsSolo; при _bus.IsMono сводит L/R в центр.
  * InputSource: после ToStereo при _model.IsMono усредняет L/R (до resample).
- МОДЕЛИ: InputChannelModel и OutputBusModel получили IsMono/IsSolo; клонирование и
  ApplyPreset расширены (плюс пересчёт solo-флагов после пресета).
- VIEWMODELS: `OutputOptionViewModel` (чекбокс выхода, toggle → SetRoute) вместо
  удалённого `BusRouteViewModel.cs`. `InputChannelViewModel` переписан: HardwareOutputs/
  VirtualOutputs (разделение по имени: "cable"/"vb-audio"/"virtual"), тексты кнопок
  OUT n/N ▾ и VIRT n/N ▾, HasHardware/HasVirtual, mono/solo → модели (+solo через движок).
  `OutputBusViewModel` теперь принимает engine для SetOutputSolo.
- VIEWS: MainView.xaml полностью переписан (стрипы-колонки 138px, VU-мeтp вертикальный
  PeakToHeight+градиент green→yellow→red, вертикальный фейдер -60..+12, LED ToggleButton
  с кастомным ControlTemplate, RouteBtn, попапы через Button.Tag→Popup и код-бекхенд
  OnRouteButtonClick). MainView.xaml.cs — переключение Popup.IsOpen.
- Конвертер: новый PeakToHeightConverter (та же db-логарифм-шкала, что PeakToWidth).
- Сборка проекта: EXIT=0 (только CS0618 про obsolete WasapiOut/Capture).
- Смоук-тест движка (харнесс в Temp, добавлен SoloState.cs): RefreshDevices (44 in/25 out),
  Start, SetRoute, volume/mute/mono стрипа, SetInputSolo/SetOutputSolo, bus mono,
  SetRoute off, Stop, CreateSnapshot, Dispose — всё без исключений.

### Pending
- Для дока OBS: закрыть OBS и нажать «Install dock in OBS» в SoundMeeter (кнопка
  «OBS» в тулбаре) → перезапустить OBS → View → Docks → SoundMeeter. Живой прогон
  окна настроек и панели в самом OBS не сделан (нужен перезапуск OBS).
- Для обновлений: сделать первый релиз в `IDerkBot/SoundMeeter` с portable-zip,
  поднять `<Version>` выше 1.0.0 и живым прогоном проверить баннер → окно →
  «Установить» → авто-перезапуск.
- Живой визуальный прогон UI на машине пользователя: раскладка стрипов, попапы OUT/VIRT,
  вертикальные метры, поведение SOLO (non-solo замолкают). Глянуть тёмную тему CheckBox в
  попапе (default-стиль может быть неидеальным), при необходимости стилизовать.
- Подумать про сохранение позиции буфера/баллы (не критично).
- Проверить, что режет ли "CABLE Input" имена: классификация виртуальных по
  cable/vb-audio/virtual — на машине юзера корректно разделила (аппаратные vs VAC/VB).
- Связка половинок кабеля (связано с «Resolved (этот turn)» выше) — живьём не
  проверено, глянуть на машине с VB-Cable/VAC:
  (1) «Refresh devices» → пропал ли «SPK CABLE-x Output», а его приложения остались
  в канале «MIC CABLE-x Input» и слышны;
  (2) перенос приложения на стрип входа кабеля не переписывает источник стрипа и
  роутит приложение в «CABLE-x Output» (в логе видно `apps->` и `cable-peer=`).

---

## Цель (общая)
```
SoundMeeter = модуль в репозитории StreamerTools\Modules\SoundMeeter
Главная идея (как VoiceMeeter Potato):
- НЕСКОЛЬКО входных каналов с выбором источника (микрофон/loopback вывод/виртуальный кабель)
- Матрица маршрутизации: каждый вход может быть направлен в ЛЮБУЮ/НЕСКОЛЬКО выходных шин
- Всё накладывается на систему через WASAPI
- UI: тёмный, в стиле VoiceMeeter, «живые» метры уровней
```
Внутри проекта SoundMeeter это называлось «микшер»: маршрутизация ROUTER задается матрицей: 
для каждого входа и каждой шины есть Enabled + GainDb.

## ТРЕБОВАНИЯ (от пользователя, в свободной формулировке)
1. Никаких авто-фокусов: не привязывать выбор к системному устройству по умолчанию.
2. «Сколько независимых выходов юзер хочет — столько и будет»: это VoiceMeeter вряд ли заменит
   (там дают VB-Cable как ноль-шину), но пусть юзер делает сколько угодно output-шины.
3. Интерфейс = квадратная матрица: по строкам входные стрипы, по столбцам output strip'ы.
4. Входная шина = WASAPI loopback-захват на суще‑ствующем устройстве/микрофоне
   (NATIVE для стримера: список каналов с Volume, Mute, таким-то маршрутированиям).
5. «Одно-единственное требование»: в любой момент юзер может включить/отключить маршрутизацию
   источника в конкретный стрип, «не трогая остальные» → live-правила применяются мгновенно,
   отдельно, независимо.

## Архитектура (текущая)
```
InputSource (захват: WasapiCapture / WasapiLoopbackCapture, декодирование PCM16/24/32/float,
            сведение в стерео 48кГц, опц. Mono-свод) --> SampleRingBuffer (общий буфер, N курсоров)
   Каждый курсор Buffer == BusTap : ISampleProvider (громкость входного стрипа + mute + solo-правило,
            ВСЕГДА возвращает buffer.Length!)
            --> BusDsp : ISampleProvider (обёртка MixingSampleProvider, bus volume/mute/mono/solo, peak)
            --> WasapiOut (Shared, event-sync, latency 100ms) -- ИСХОДНАЯ ШИНА
```
Mаршрутизация: у каждого входа BusRouting: Dictionary<bus.DeviceId, BusRouting{Enabled}> —
ЕДИНСТВЕННЫЙ источник "в текущую ли шину идёт вход". GainDb в аудио-пути больше НЕ используется.
SoloState (общий) — volatile флаги "есть активный solo для группы" (входы/выходы раздельно).

## Files & key points
```
Modules/SoundMeeter/
  SoundMeeter.csproj    — WinExe, net10.0-windows, CommunityToolkit.Mvvm 8.4.2,
                          Microsoft.Extensions.DependencyInjection 10.0.12, NAudio 3.1.0
                          (системные аксессуары: Volatile, Volatile.Read — без внешних пакетов)
  App.xaml / .cs        — DI: SettingsService, WasapiAudioEngine, MainViewModel, MainWindow
  Views/MainView.xaml   — VoiceMeeter-стрипы: колонки входов (INPUTS) и выходов (OUTPUTS),
                          горизонтальная прокрутка; у стрипа: VU-метр вертикальный
                          (PeakToHeight + градиент green/yellow/red), вертикальный фейдер,
                          dB-подпись, LED M/S/MUTE; у входа попапы OUT ▾/VIRT ▾ (чекбоксы,
                          мульти-выбор, live). Классификация виртуальных: cable/vb-audio/virtual.
  Views/MainView.xaml.cs — OnRouteButtonClick: Button.Tag=Popup → toggle IsOpen
  Views/MainWindow.xaml — заголовок SoundMeeter, тёмный фон #1E1E1E, Closed->SaveNow
  ViewModels/           — MainViewModel, InputChannelViewModel, OutputOptionViewModel (новый),
                          OutputBusViewModel; удалены BusRouteViewModel, RoutingItem
  Converters/           — BoolToStartStop, PeakToHeight (новый), BoolToOpacity, InverseBoolToVisibility
  Services/IAudioEngine — RefreshDevices / Start / Stop / SetRoute(inputId, busDeviceId, enabled)
                          / SetInputSolo / SetOutputSolo / CreateSnapshot / ApplyPreset / EnsureRoutingTable
  Services/WasapiAudioEngine.cs — enumeration: MIC ... + SPK ... ; Buses = все render-устройства;
                          solo-флаги через SoloState под _gate; live-apply в Start/PathRW;
                          orphan-source cleanup; Dispose
  Services/SettingsService.cs — %APPDATA%\SoundMeeter\settings.json, автосейв 2s + на закрытии
  Models/               — InputChannelModel(+IsMono/IsSolo), OutputBusModel(+IsMono/IsSolo),
                          BusRouting(Enabled/GainDb — GainDb больше не применяется), AppSettings
                          (+ObsDock), ObsDockSettings/ObsDockChannelRef, ObsDockMessages (протокол дока)
  Audio/                — SampleRingBuffer(+RingCursor), SoloState (новый), InputSource
                          (Mono-свод), BusTap (solo+mute), BusDsp (solo+mono)
  Services/IObsDockServer.cs + Services/ObsDockServer.cs — сервер док-панели: свой мини-HTTP
                          (TcpListener: /, /dock.css, /dock.js, /api/state) + WebSocket (101,
                          кадры, чанкинг, ping/pong); push снимка 30 Гц, команды vol/mute/solo/mono
  Services/ObsDockInstaller.cs — ObsDockInstaller (путь %APPDATA%\obs-studio, IsObsRunning,
                          гвард «OBS закрыт») + ObsDockConfig (правка [BasicWindow]
                          ExtraBrowserDocks в user.ini, upsert по uuid, бэкап)
  Resources/obs-dock/    — index.html/dock.css/dock.js панели (EmbeddedResource, LogicalName
                          "obs-dock.*")
  ViewModels/MainViewModel.ObsDock.cs — сбор снимка по настройкам, публикация из таймера
                          метров, применение команд в UI-потоке
  ViewModels/ObsDockSettingsViewModel.cs + Views/ObsDockSettingsWindow.xaml — настройки дока
  Views/Controls/MixerToolbarView.xaml — кнопка «OBS» (настройки дока)
```
Ключевые НАШИ правки (смотри СЕЙЧАС, не перечитывая старые ветки):
- BusTap.Read: ВСЕГДА полная длина (иначе MixingSampleProvider удалит вход)
- BusDsp/BusTap: solo-правило через SoloState (входы и выходы отдельно)
- InputSource: Mono-свод после ToStereo, до реземпла
- Кнопки OUT/VIRT = Popup + CheckBox (IsEnabled→SetRoute live), счётчики n/N ▾ в заголовке

## Known pitfalls / жопа-места (НАШИ замечания)
- NAudio 3.1.0: MixingSampleProvider сам удаляет источники, вернувшие < запрошенного =>
  BusTap обязан «добивать» нулями.
- WasapiLoopbackCapture: НЕЛЬЗЯ захватывать свой собственный output (feedback-lоб).
- apps must map: «CABLE Input» becomes InputDevice при выборе как output.
- WasapiOut.Init: единственная сигнатура IWaveProvider; бридж ISampleProvider —
  явный `new SampleToWaveProvider(dsp)` (3.1.0). (Иначе — сомнительный implicit path.)

## Git / статусы
- Модули: AudioRouter/ (untracked, отдельный git), ObsMusicPlayer/ (untracked), SoundMeeter (tracked).
- Ветка: main.
- НЕ коммитить без явного запроса.