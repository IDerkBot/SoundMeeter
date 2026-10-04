# SoundMeeter

Открытый микшер звука для Windows — для стримеров и подкастеров, «аппаратный пульт»
в виде программы, в духе VoiceMeeter.

SoundMeeter одновременно снимает звук с нескольких источников (микрофоны, лупбек через
WASAPI с устройств вывода, половинки виртуальных кабелей), независимо направляет каждый
из них в одну или несколько выходных шин, применяет к полосам обработку и показывает
уровни в реальном времени. Управлять им можно с MIDI-контроллера и из док-панели
внутри OBS Studio.

```
mic ──┐                                    ┌─► полоса 1 ─┐
      ├─► Шумоподавление ─► Компрессор ─► ─┤             ├─► Шина 1 ─► наушники
loop ─┤                    ─► Задержка ─► ─┤             │
      │                    ─► Реверберация ─┤             │
      │                                   └─► полоса 2 ─┘
cable ┘                                             └─► Шина 2 ─► трансляция
```

**[English](README.md)**

---

## Возможности

**Микширование**

- Полосы входов и выходные шины — отдельными переставляемыми колонками
- У каждой полосы: вертикальный фейдер, сегментированный VU-метр, показание в дБ
- Светодиодные кнопки: **Без звука**, **Соло**, **Моно** (соло общее для входов и шин)
- Направление каждой полосы в физические выходы (`OUT`) и входы виртуального кабеля
  (`VIRT`), оба с множественным выбором
- Две настраиваемые кнопки **FUNC** на каждой входной полосе — мгновенные пресеты
  маршрутизации
- Живое управление по MIDI: привязка к CC, нотам и PitchWheel, режимы Toggle / Hold / Latch

**Обработка (для каждой входной полосы)**

- Усиление
- Графический эквалайзер — 10 полос (31 Гц…16 кГц) с кривой в отдельном окне,
  5 пресетов кривой, срез снизу, срез сверху и общее компенсирующее усиление
- Компрессор — порог, соотношение, атака, release, компенсирующее усиление
- Задержка — время, обратная связь, затухание, микс
- Реверберация — размер, затухание, микс
- Шумоподавление на RNNoise — процент подавления, dry/wet, 4 формантных полосы эквалайзера

**Маршрутизация и интеграция**

- Маршрутизация по приложениям: перетащите работающее приложение на полосу, чтобы снять
  звук только с него
- Список установленных программ читается из реестра; ключевые слова фильтруют системный
  шум
- Автосопряжение виртуальных кабелей (VB-Cable, VAC, StreamerCable) — находит обе
  половинки кабеля по имени и не даёт использовать выход кабеля как вход полосы
- **Док-панель для OBS Studio** — встроенный локальный HTTP- и WebSocket-сервер
  отправляет состояние полос в Browser Dock с частотой 30 Гц и принимает обратно команды
  громкости, mute, solo и mono
- Автообновление с GitHub Releases (портативный ZIP, самораспаковывающаяся замена
  через `robocopy /MIR`)

**Приложение**

- Значок в системном лотке, при желании сворачивание в трей
- Автозапуск вместе с Windows с правами администратора (задача планировщика, без запроса UAC при входе) и защита от второго экземпляра
- Интерфейс на английском и русском
- Просмотр журнала с ротацией файлов

---

## Требования

| | |
|---|---|
| ОС | Windows 10 / 11, только **x64** |
| SDK | .NET 10 SDK с рабочей нагрузкой Windows Desktop (`net10.0-windows`) |
| Среда выполнения | .NET 10 Desktop Runtime |
| Права | **Администратор** — требуется `app.manifest` (`requireAdministrator`) |
| Опционально | Виртуальный аудиокабель (VB-Cable / VAC / StreamerCable), OBS Studio, MIDI-контроллер |

Только Windows — это осознанное решение: все проекты нацелены на `net10.0-windows` /
`win-x64` и зависят от WASAPI, WinRT/COM-политики аудио, реестра и WinForms. Кроссплатформенной
сборки и `#if`-веток нет.

---

## Сборка

```powershell
# из корня репозитория
dotnet build src\SoundMeeter.slnx -c Release

# или только приложение
dotnet build src\SoundMeeter.App\SoundMeeter.App.csproj -c Release
```

Решение использует новый XML-формат `.slnx`, поэтому нужен свежий SDK
(разрабатывалось на 10.0.401).

Общая библиотека стилей `StreamerTools.Style` — это git-сабмодуль в
`src/StreamerTools.Style`. Перед первой сборкой его нужно инициализировать:

```powershell
git submodule update --init
```

### Запуск

```powershell
dotnet run --project src\SoundMeeter.App\SoundMeeter.App.csproj
```

или запустите готовый исполняемый файл напрямую (он запросит повышение прав):

```
src\SoundMeeter.App\bin\Release\net10.0-windows\win-x64\SoundMeeter.exe
```

Проект называется `SoundMeeter.App`, но имя сборки и исполняемого файла — `SoundMeeter`:
имя exe входит в контракт релиза (обновление ищет `SoundMeeter.exe` внутри portable-архива
и перезапускает процесс именно по этому имени), поэтому переименование — отдельная
задача, и начинать её надо с `UpdateApplier`, а не с csproj.

---

## Тесты

```powershell
dotnet test src\SoundMeeter.slnx
```

120 тестов на xUnit в шести проектах; аудиооборудование не требуется:

| Проект | Тестов | Что проверяет |
|---|---|---|
| `SoundMeeter.App.Tests` | 54 | Представления и элементы WPF (STA-хост), сброс параметров, попапы эффектов, значки приложений, границы модулей и слоёв |
| `SoundMeeter.Core.Tests` | 11 | ViewModel, MIDI-привязки, восстановление пресета через движок, «ядро остаётся без WPF» |
| `SoundMeeter.Audio.Tests` | 31 | Блоки DSP, кольцевой буфер, мигратор настроек, отсутствие WPF в аудиомодуле |
| `SoundMeeter.Update.Tests` | 18 | Сравнение семантических версий и выбор файла релиза |
| `SoundMeeter.ChangeLanguage.Tests` | 6 | Что все ключи локализации находятся и все языковые файлы полны |

xUnit v2 не умеет запускать STA-поток, поэтому UI-тесты идут через
`SoundMeeter.App.Tests/Infrastructure/UiHost.cs`, который поднимает один STA-поток с
настоящим `Application` и установленной локализацией.

CI-конвейера нет — тесты запускаются вручную. Это зафиксированная известная пробел.

---

## Структура проекта

```
src/
├── SoundMeeter.App/              оболочка WPF (WinExe, сборка «SoundMeeter»)
│   ├── App.xaml.cs               точка сборки зависимостей, порядок запуска
│   ├── Views/                    MainView разбит на UserControl'ы + окна
│   ├── Views/Controls/           представления полос, словарь ресурсов MixerTheme
│   ├── Controls/                 SegmentedMeter, GainKnob, HorizontalFillPanel
│   ├── Converters/               конвертеры значений
│   ├── Services/LocResources.cs  подключение строк локализации к Application.Resources
│   └── Resources/                значок приложения
├── SoundMeeter.Core/             ViewModel + сервисы (слой логики, без WPF)
│   ├── ViewModels/               MainViewModel разбит на 6 partial + VM отдельных полос
│   ├── Services/                 аудиодвижок, настройки, MIDI, сервер док-панели
│   ├── AudioPolicy/              interop с недокументированным AudioPolicyConfig
│   └── Resources/                фильтр приложений (копируется в выход), встроенные ресурсы док-панели
├── SoundMeeter.Audio/            ядро звука + модели (без WPF)
│   ├── Audio/                    захват, кольцевой буфер, отводы, цепочка DSP, RNNoise
│   └── Models/                   схема настроек, мигратор (v0 → v5)
├── SoundMeeter.Logger/           логирование в ротируемый файл
├── SoundMeeter.ChangeLanguage/   строки EN/RU (Loc)
├── SoundMeeter.Update/           автообновление с GitHub Releases
├── SoundMeeter.StartUp/          автозапуск + единственный экземпляр
├── SoundMeeter.TrayIcon/         NotifyIcon
└── *.Tests/                      шесть тестовых проектов
```

### Правила модулей

Зависимости направлены только в одну сторону, и это проверяется тестами, а не
соглашением (`ModuleBoundaryTests`, `AudioModuleBoundaryTests`, `CoreWpfFreeTests`
анализируют `Assembly.GetReferencedAssemblies()`):

- Ни один функциональный модуль не ссылается на другой функциональный модуль
- Ни один модуль не ссылается ни на ядро, ни на приложение
- `SoundMeeter.Logger` и `SoundMeeter.ChangeLanguage` — базовый слой и не ссылаются ни на что своё
- Ядро не ссылается на приложение, а приложение ссылается на ядро
- Ядро не ссылается на UI-модули (`SoundMeeter.TrayIcon`, `StreamerTools.Style`)
- **Ни ядро, ни `SoundMeeter.Audio` не должны тянуть WPF** — `PresentationFramework`,
  `PresentationCore`, `WindowsBase` и `System.Xaml` запрещены даже транзитивно
  (`CoreWpfFreeTests`, `AudioModuleBoundaryTests`)


```
              ┌──────────────────────────┐
              │ SoundMeeter.App (WPF)    │  WinExe, сборка «SoundMeeter»
              └───────┬──────────┬───────┘
                      ▼          │
              ┌──────────────────────────┐
              │     SoundMeeter.Core    │  ViewModel + сервисы
              └────────┬─────────────────┘
      ┌─────────┬───────┼────────┬──────────┬───────────┐
      ▼         ▼       ▼        ▼          ▼           ▼
   Audio     Logger  ChangeLang Update   StartUp     TrayIcon ← только App
      └──────►  └──────┴─────┴──────────┘
                  (базовый слой — без исходящих связей)
```

**Ядро свободно от WPF.** `SoundMeeter.Core` не объявляет `UseWPF`, а `CoreWpfFreeTests`
запрещает `PresentationFramework`, `PresentationCore`, `WindowsBase` и `System.Xaml` в любом
месте графа ссылок ядра. Поэтому тесты ядра идут без UI-потока, без `Application` и без окна.

Дорога к этому — пять зависимостей от WPF, закрытые интерфейсами:

| Было в ядре | Стало |
|---|---|
| `BitmapImage`/`ImageSource` для значков и P/Invoke `SHGetFileInfo`, продублированный в трёх ViewModel | `FileIconConverter` / `ProcessIconConverter` и одна копия в `ShellIcons` на стороне UI; ядро отдаёт только путь или PID |
| `IAudioService.GetAppIcon(uint) → BitmapImage` (не вызывался никем) | удалён вместе с кэшем значков и `System.Drawing` |
| `Application.Current.Dispatcher` в `LocalizedViewModel` | `IDispatcherService` (`Post` + `HasThreadAccess`) там, где события действительно фоновые: MIDI и команды дока OBS |
| `DispatcherTimer` метров | `IUiTimer`, реализация `DispatcherTimerAdapter` в приложении |
| `ICollectionView` с `Filter` для поиска по установленным программам | обычный `ObservableCollection`, пересобирается явно |
| `Clipboard.SetText` в двух ViewModel | `IClipboardService` |

Пространства имён остались прежними (`SoundMeeter.ViewModels`, `SoundMeeter.Services`,
`SoundMeeter.Views.Controls`): разделение меняет сборки, а не пространства имён, поэтому ни
один `using` и ни одна `clr-namespace` в XAML не менялись.

---

## Путь аудиосигнала

```
InputSource (WasapiCapture | WasapiLoopbackCapture)
  → декодирование PCM16/24/32/float → сведение в стерео 48 кГц → опциональное сведение в моно
  → DenoiserDsp (RNNoise) → StripDsp (эквалайзер → компрессор → усиление → задержка → реверберация)
  → SampleRingBuffer.Write()                     [кольцо 1 с, 48 кГц × 2 канала]
        │
        ├─ RingCursor → BusTap (на каждый вход × шину) : ISampleProvider
        │                 громкость = muted ? 0 : DbToLinear(input.VolumeDb)
        │                 соло определяется через общее SoloState
        │
        └─ RingCursor → ...
        ▼
  BusDsp : ISampleProvider (обёртка над MixingSampleProvider)
        ▼
  WasapiOut (Shared, событийный, ~100 мс задержки) → физическое устройство
```

Неочевидные ограничения, о которых стоит знать до правок движка:

- **`BusTap.Read` обязан всегда возвращать `buffer.Length`.** NAudio-овский
  `MixingSampleProvider` молча **удаляет** любой источник, вернувший меньше запрошенного,
  поэтому короткое чтение не сделает полосу тихой, а уберёт её из микса совсем.
- **Нельзя снимать лупбеком собственный выход движка** — мгновенная обратная связь.
- `WasapiOut.Init` требует явного `new SampleToWaveProvider(dsp)`; в NAudio 3.1.0 нет
  неявного преобразования.
- Экземпляры `MMDevice` нужно явно освобождать в `RefreshDevices`, иначе накапливаются
  утечки COM.
- `GainDb` маршрута в аудиопути **не применяется** — единственный источник усиления
  это громкость полосы.

---

## Настройки

Вся конфигурация хранится на диске; переменные окружения в коде не читаются нигде.

| Что | Где |
|---|---|
| Настройки | `%APPDATA%\SoundMeeter\settings.json` |
| Журналы | `%LOCALAPPDATA%\SoundMeeter\logs\` (с ротацией) |
| Автозапуск | Задача планировщика `\SoundMeeter` (триггер «вход в систему», `RunLevel=Highest`; оставшаяся запись в `HKCU\...\Run` переносится в неё) |
| Регистрация док-панели | `%APPDATA%\obs-studio\user.ini` → `[BasicWindow] ExtraBrowserDocks` |
| Ключевые слова фильтра | `Resources/system_apps_filter.json` (копируется в вывод) |
| Единственный экземпляр | Мьютекс `Local\SoundMeeter.SingleInstance.<user>` + событие показа окна |

Заметное поведение `settings.json`:

- Пишется **атомарно** — `settings.json.tmp` + `File.Replace`, затем перечитывание для проверки
- Повреждённый файл **изолируется** в `settings.corrupt.json`, а не удаляется молча
- Версия схемы отслеживается (`AppSettings.SchemaVersion`, сейчас **5**) и переносится
  цепочкой шагов `MigrateStep`; файл из *более новой* сборки никогда не перезаписывается,
  поэтому откат версии не испортит настройки
- Автосохранение по таймеру раз в 2 с, по флагу изменения, вне UI-потока

### Док-панель OBS

Включается кнопкой Settings на панели инструментов. Приложение поднимает
HTTP- и WebSocket-сервер только на loopback (порт по умолчанию **17954**) и добавляет
запись Browser Dock в `user.ini` OBS под фиксированным UUID. Он отказывается править файл,
пока запущен OBS, или если INI повреждён, и перед записью создаёт резервную копию
`user.ini.soundmeeter.bak`.

Сервер написан вручную на `TcpListener`: `HttpListener` не умеет апгрейд до WebSocket,
а Kestrel сломал бы single-file публикацию. Ресурсы встроены в сборку, а язык панели
передаётся через внедрённый `window.SM_I18N`.

---

## Стек технологий

C# / .NET 10, WPF, генераторы исходников `CommunityToolkit.Mvvm`, NAudio 3.1.0, RNNoise
через P/Invoke, `Microsoft.Extensions.DependencyInjection`, `System.Text.Json`.

| Пакет | Версия | Назначение |
|---|---|---|
| `NAudio` | 3.1.0 | Захват/лупбек/вывод WASAPI, MMDevice, MIDI |
| `CommunityToolkit.Mvvm` | 8.4.2 | `[ObservableProperty]`, `[RelayCommand]` |
| `Microsoft.Extensions.DependencyInjection` | 10.0.12 | Точка сборки зависимостей |
| `Microsoft.Extensions.Logging(.Abstractions)` | 10.0.11 | `ILogger` |
| `YellowDogMan.RRNoise.NET` | 0.1.9 | Поставляет нативную `rnnoise.dll` |
| `xunit` / `xunit.runner.visualstudio` | 2.5.3 | Тесты |

RNNoise вызывается через собственный P/Invoke (`RnNoiseInterop`), а не через управляемую
обёртку `Denoiser` из пакета: обёртка прячет своё внутреннее кольцо буфера, из-за чего
сухой и шумоподавленный сигналы расходятся по времени.

---

## Другие известные проблемы

- **Нет файла LICENSE.** Его нужно добавить до публикации репозитория.
- **Нет CI.** Тесты запускаются только вручную.
- **Нет руководства пользователя.** Использование нигде не описано, кроме этого файла.
- **Расхождение версий.** `SoundMeeter.App.csproj` объявляет `0.0.1`, `app.manifest`
  жёстко содержит `1.0.0.0`, а в заметках первым релизом задумывалась `1.0.0`. Манифест
  встраивается SDK как есть, поэтому **не** пытайтесь подставить в него свойство MSBuild —
  Windows откажется запускать файл.
- **`IAudioEngine` — интерфейс на 25 членов.** Известный рефакторинг: разбить на
  `IRoutingTable`, `IDeviceCatalog`, `IRStripTopology` и `IPresetStore`.
- **`Loc` и `AppLog` — статические**, примерно в 99 местах ViewModel, что мешает
  тестировать ViewModel'ы.
- **Автообновление жёстко привязано** к `IDerkBot/SoundMeeter` константами времени
  компиляции и требует опубликованного релиза на GitHub.
- **Значки не проверяются на уровне ViewModel** — и не должны: они живут в конвертерах
  UI (их покрывает `AppIconTests`), а ядро знает только путь и PID.

В `Obsidian/StreamerTools/` лежат рабочие заметки автора (карта архитектуры, журнал
`LAST_ACTION`, бэклог и список граблей NAudio) на русском языке. Полезно, но не
поддерживается как пользовательская документация.

---

## Лицензия

**Пока не определена.** Файла `LICENSE` в репозитории нет. Сторонние зависимости
используются на условиях собственных лицензий (NAudio — MIT, RNNoise — BSD-3-Clause,
CommunityToolkit.Mvvm — MIT).
