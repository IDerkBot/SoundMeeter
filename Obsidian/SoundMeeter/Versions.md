# SoundMeeter — Versions

Changelog. For each version: what was new compared to the previous one.

---

## 0.0.1 → current (unreleased)

Baseline: `4f69ded` *"fix close button for obs"* (2026-09-28).
Head: `3a3cd84` *"decompile on core and app"* (2026-10-02).

0.0.1 was a single monolithic WPF project with the mixer, OBS dock, MIDI bindings,
RNNoise denoising, per-app routing and auto-update already working. Everything below is
what landed after it.

---

## English

### New features

**Strip DSP chain (SM-B05)** — four new per-input effects, in addition to the RNNoise
denoiser that 0.0.1 already had:

- Compressor — threshold, ratio, attack, release, makeup gain
- Trim gain — two-sided −60…+24 dB, applied after the compressor
- Delay — time, feedback, damping, mix
- Reverb — size, damping, mix

All four are processed in a fixed order `denoiser → compressor → trim → delay → reverb`,
assembled once per source in `StripDsp`. Effects before the bus split are computed once per
packet, not once per send, so sends at different levels do not sound different; the
latency-bearing effects go last so the compressor does not duck its own tail. Every knob is
live — block parameters are read from the preset model on each packet, no stream rebuild.
Stateful blocks (compressor envelope, delay lines, reverb) are reset on source start so a
restart does not continue a stale tail. NaN/Inf are gated and amplitude clamped before the
ring buffer, because a single NaN in the ring would silence every send of the strip.

Effects share one view (`StripEffectSettingsView`) and one ViewModel shape
(`StripEffectViewModel` / `EffectKnobViewModel`), so a fifth effect touches neither the UI
nor the processing code. The effect button in the strip column toggles on/off; right-click
opens the knob popup, same as DEN.

**FUNC buttons** — two configurable buttons per input strip for instant routing presets.
A press applies the assigned outputs (hardware `OUT` and/or virtual `VIRT`); a second press
removes them and restores the routing that was in effect before the first press. Two apply
modes: *only mine* (routing is brought to exactly the button's list — the "to headphones" /
"to stream" one-click scenario) and *append* (own outputs are added, foreign ones untouched).
Assignment and routing are deliberately separated — the popup only writes a draft into the
model, only the press touches the strip's routes, so un-ticking the sole output in the popup
cannot silently remove audio from the strip. `FuncBaseRouting` is stored in the preset, not
just in memory, so a pressed button survives a restart and can still be released back.

**Tray icon and application lifecycle** — a `NotifyIcon` in the notification area with
show / toggle-run / exit menu, tooltips capped at the real 63-character `NotifyIcon` limit,
and the icon loaded from the running executable (so it is not blank in a portable build).
Closing the window minimizes to the tray instead of exiting; a balloon notification is used
for errors.

**Run at startup** — a scheduled task `\SoundMeeter` with a logon trigger at
`RunLevel=Highest`, so no UAC prompt at logon. A leftover `HKCU\...\Run` record is migrated
to it. The registry is the source of truth for the actual state, the settings file only
stores the wish, so a copied `settings.json` cannot lie about it.

**Single instance guard** — `Local\SoundMeeter.SingleInstance.<user>` mutex plus a
show-window event, so a second launch raises the existing window instead of fighting it.

**Double-click to reset a parameter** — `ParamReset`, an attached property that returns a
fader, slider or knob to its own default. Implemented as an attached property rather than a
style handler because a strip has a dozen controls and each has its *own* default (send
0 dB, Noise Remover 100 %, compressor −18 dB). The default is known by the parameter's
ViewModel, not by the view.

**Application icon** — `Resources/Icon.ico` embedded as the app icon; the tray icon reuses
the same one.

### Fixes

**COM / `MMDevice` leak (SM-A08)** — `MMDevice` is a COM wrapper around `IMMDevice` with
**no finalizer**: without an explicit `Dispose()` the reference is never released. The device
catalogue is walked on a 2-second timer, so hundreds of thousands of COM objects accumulated
per day and the process grew to gigabytes. `AudioService` and `WasapiAudioEngine` now
dispose every `MMDevice`, every `AudioSessionManager` and the enumerator itself, keeping
only string copies of Id and name. The one deliberate exception is documented in place: the
device a `WasapiOut` is built for is owned by that bus until it closes, and disposing it
early would be a use-after-dispose.

### Engineering

**Project decomposition** — one monolithic `SoundMeeter.csproj` became nine projects under
`src/` plus five test projects, in the new XML `.slnx` solution format:

```
SoundMeeter.App           WPF shell (WinExe, assembly "SoundMeeter")
SoundMeeter.Core          ViewModels + services (logic, no WPF)
SoundMeeter.Audio         audio core + models (WPF-free)
SoundMeeter.Logger        rotating file logger
SoundMeeter.ChangeLanguage EN/RU strings
SoundMeeter.Update        GitHub Releases auto-update
SoundMeeter.StartUp       run-at-startup + single instance
SoundMeeter.TrayIcon      NotifyIcon
StreamerTools.Style       shared style library (git submodule)
```

**The core is now WPF-free.** `SoundMeeter.Core` no longer sets `UseWPF`, and five WPF
dependencies were pushed behind interfaces: `IDispatcherService`, `IUiTimer`,
`IClipboardService`, installed-apps enumeration, and icon loading.
`IAudioService.GetAppIcon(uint) → BitmapImage` — dead code with a cache and a `System.Drawing`
dependency behind it — was deleted outright; icons moved to `FileIconConverter`,
`ProcessIconConverter` and one `ShellIcons` copy on the UI side, and the core now knows only
paths and PIDs. Namespaces were left untouched, so no `using` and no XAML `clr-namespace`
had to change — the split moves assemblies, not namespaces.

**Module boundaries are enforced by tests, not convention.** `ModuleBoundaryTests`,
`AudioModuleBoundaryTests` and `CoreWpfFreeTests` inspect
`Assembly.GetReferencedAssemblies()` and reject: one feature module referencing another,
any module referencing the core or the app, the core referencing the UI modules, and WPF
assemblies (`PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml`)
reaching the core or the audio module even transitively. `SoundMeeter.Logger` and
`SoundMeeter.ChangeLanguage` are the base layer and reference nothing of their own.

**Settings schema v1 → v5.** The migrator gained steps for the effect parameters, the FUNC
buttons and their base routing, and the app-behaviour settings (`TrayEnabled`,
`RunAtStartup`). Effect values loaded from an older file are clamped to the ranges the DSP
itself enforces, so a hand-edited or corrupted `settings.json` cannot push NaN or ×100
gain into the audio buffer. A file from a *newer* build is still never overwritten.

**Other cleanups**

- `StreamerTools.Style` became a git submodule at `src/StreamerTools.Style`
- `MainViewModel` split into six partials plus per-strip ViewModels; `MainViewModel.Behaviour`
  owns tray and auto-start state without knowing about `NotifyIcon` or the registry
- `LocalizableViewModel` refreshes through `IDispatcherService` where events really do arrive
  off the UI thread — MIDI and OBS dock commands
- The installed-apps search is a plain `ObservableCollection` rebuilt on demand, not an
  `ICollectionView` with a filter
- Localized strings moved into `SoundMeeter.ChangeLanguage`, merged into
  `Application.Resources` by `LocResources`
- `README.md` / `README.ru.md` added, with the audio signal path and the non-obvious NAudio
  constraints written down

### Tests

120 xUnit tests across six projects, none of which need audio hardware:

| Project | Tests | Covers |
|---|---|---|
| `SoundMeeter.App.Tests` | 54 | WPF views and controls (STA host), param reset, effect popups, FUNC buttons, app icons, module and layer boundaries |
| `SoundMeeter.Audio.Tests` | 31 | DSP blocks, ring buffer, settings migrator, WPF-free boundary checks |
| `SoundMeeter.Update.Tests` | 18 | Semantic version comparison and release-asset selection |
| `SoundMeeter.Core.Tests` | 11 | ViewModels, MIDI bindings, preset restore through the engine, "core stays WPF-free" |
| `SoundMeeter.ChangeLanguage.Tests` | 6 | Every localization key resolves; every language file is complete |

xUnit v2 cannot host an STA thread, so the UI tests run through
`SoundMeeter.App.Tests/Infrastructure/UiHost.cs`, which spins up a single STA thread with a
real `Application` and localization installed.

---

## Русский

### Новые возможности

**Цепочка DSP на стрипе (SM-B05)** — четыре новых эффекта на каждый вход, поверх
денойзера RNNoise, который уже был в 0.0.1:

- Компрессор — порог, коэффициент сжатия, атака, отпускание, makeup-gain
- Trim-усиление — двустороннее, −60…+24 дБ, применяется после компрессора
- Задержка — время, обратная связь, гашение верхов, mix
- Реверберация — размер, затухание верхов, mix

Все четыре обрабатываются в фиксированном порядке `денойзер → компрессор → trim → задержка → реверберация`, цепочка собирается один раз на источник в `StripDsp`. Эффекты
перед разветвлением на шины считаются один раз на пакет, а не в каждой посылки — иначе компрессор и реверберация пересчитывались бы заново для каждой шины и посылки с разной громкостью звучали бы по-разному. Эффекты с задержкой стоят последними, иначе компрессор слышал бы собственное эхо и сжимал не голос, а хвост. Параметры блоков читаются из модели пресета на каждом пакете, поэтому любая крутилка слышна сразу, без пересоздания аудиопотока.
Блоки с состоянием (огибающая компрессора, линии задержки и реверберации) сбрасываются при старте источника, чтобы новый запуск начинался «с нуля», а не продолжал старый хвост. Перед кольцевым буфером NaN/Inf гасятся, а амплитуда ограничивается: одна NaN в кольце распространилась бы на все посылки стрипа и заглушила канал целиком.

Все эффекты делят одну разметку (`StripEffectSettingsView`) и одну форму ViewModel (`StripEffectViewModel` / `EffectKnobViewModel`), поэтому добавление пятого эффекта не затронет ни UI, ни код обработки. Кнопка эффекта в колонке стрипа — переключатель вкл/выкл, правый клик открывает попап с крутилками, как у DEN.

**Кнопки FUNC** — две настраиваемые кнопки на каждом входном стрипе для мгновенного переключения роутинга. Нажатие применяет назначенные выходы (аппаратные `OUT` и/или виртуальные `VIRT`), повторное нажатие снимает их и возвращает роутинг, который был до первого нажатия. Два режима применения: «только свои» — роутинг стрипа приводится ровно к своему списку (сценарий «в колонки» / «в стрим» одним щелчком), и «дописать» — свои выходы добавляются, чужие не трогаются. Назначение и роутинг разведены намеренно: отметки в попапе пишут заготовку в модель, а роуты стрипа трогает только нажатие — иначе снятая галка с единственного выхода молча убрала бы звук со стрипа, а кнопка выглядела бы сломанной. `FuncBaseRouting` хранится в пресете, а не только в памяти, поэтому после перезапуска нажатая кнопка остаётся включённой и её всё ещё можно снять.

**Значок в трее и жизненный цикл приложения** — `NotifyIcon` в области уведомлений с меню «показать окно / свернуть-развернуть / выход», подсказка ограничена реальным пределом `NotifyIcon` в 63 символа, значок берётся из запущенного исполняемого файла (иначе в portable-сборке он был бы пустым). Закрытие окна сворачивает микшер в трей вместо выхода; для ошибок используется всплывающее уведомление.

**Автозапуск вместе с Windows** — запланированная задача `\SoundMeeter` с триггером на вход в систему и `RunLevel=Highest`, поэтому при входе в систему нет запроса UAC. Запись, оставшаяся в `HKCU\...\Run`, мигрируется в неё. Фактическое состояние читается из реестра, а файл настроек хранит только пожелание, поэтому скопированный `settings.json` не может наврать о нём.

**Защита от второго экземпляра** — мьютекс `Local\SoundMeeter.SingleInstance.<user>` плюс событие показа окна, поэтому второй запуск поднимает уже открытое окно, а не борется с ним.

**Двойной щелчок — сброс параметра** — `ParamReset`, присоединённое свойство, возвращающее фейдер, ползунок или крутилку к собственному значению по умолчанию. Сделано присоединённым свойством, а не обработчиком в стиле, потому что регуляторов на стрипе десяток и у каждого **свой** дефолт (у посылки — 0 дБ, у Noise Remover — 100 %, у компрессора — −18 дБ), а дефолт знает ViewModel параметра, а не представление.

**Значок приложения** — `Resources/Icon.ico` встроен как значок приложения; тот же файл используется и в трее.

### Исправления

**Утечка COM / `MMDevice` (SM-A08)** — `MMDevice` — это COM-обёртка над `IMMDevice`, у которой **нет финализатора**: без явного `Dispose()` ссылка на устройство не отпускается
никогда. Каталог устройств обходится по таймеру каждые 2 секунды, поэтому за сутки накапливались сотни тысяч COM-объектов и процесс разрастался до гигабайтов. `AudioService` и `WasapiAudioEngine` теперь освобождают каждый `MMDevice`, каждый `AudioSessionManager` и сам перечислитель, сохраняя только строковые копии Id и имени. Единственное намеренное исключение задокументировано на месте: устройство, из которого строится `WasapiOut`, принадлежит этой шине до её закрытия, и освобождение здесь было бы use-after-dispose.

### Инженерные изменения

**Декомпозиция проекта** — один монолитный `SoundMeeter.csproj` превратился в девять проектов под `src/` плюс пять тестовых проектов, в новом XML-формате решения `.slnx`:

```
SoundMeeter.App           оболочка WPF (WinExe, сборка "SoundMeeter")
SoundMeeter.Core          ViewModel'ы + сервисы (логика, без WPF)
SoundMeeter.Audio         аудиоядро + модели (без WPF)
SoundMeeter.Logger        ротируемый файловый журнал
SoundMeeter.ChangeLanguage строки EN/RU
SoundMeeter.Update        автообновление из GitHub Releases
SoundMeeter.StartUp       автозапуск + единственный экземпляр
SoundMeeter.TrayIcon      NotifyIcon
StreamerTools.Style       общая библиотека стилей (git submodule)
```

**Ядро теперь не зависит от WPF.** `SoundMeeter.Core` больше не включает `UseWPF`, и пять WPF-зависимостей ушли за интерфейсы: `IDispatcherService`, `IUiTimer`, `IClipboardService`, перечисление установленных приложений и загрузка значков. `IAudioService.GetAppIcon(uint) → BitmapImage` — мёртвый код с кэшем и зависимостью от `System.Drawing` — удалён целиком; значки переехали в `FileIconConverter`, `ProcessIconConverter` и одну копию `ShellIcons` на стороне UI, а ядро знает только пути и PID. Пространства имён не тронуты, поэтому не пришлось менять ни один `using` и ни один `clr-namespace` в XAML — разбивка переносит сборки, а не пространства имён.

**Границы модулей проверяются тестами, а не договорённостью.** `ModuleBoundaryTests`, `AudioModuleBoundaryTests` и `CoreWpfFreeTests` анализируют `Assembly.GetReferencedAssemblies()` и отвергают: ссылку одного функционального модуля на другой, ссылку любого модуля на ядро или приложение, ссылку ядра на UI-модули, а также попадание WPF-сборок (`PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml`) в ядро или аудиомодуль даже транзитивно. `SoundMeeter.Logger` и
`SoundMeeter.ChangeLanguage` — базовый слой и не ссылаются ни на что своё.

**Схема настроек v1 → v5.** В мигратор добавлены шаги для параметров эффектов, кнопок FUNC и их базового роутинга, а также настроек поведения приложения (`TrayEnabled`, `RunAtStartup`).
Значения эффектов из старого файла приводятся к диапазонам, которые проверяет сам DSP, чтобы правка `settings.json` руками не записала в аудиобуфер NaN или усиление в сотни раз. Файл из **более новой** сборки по-прежнему никогда не перезаписывается.

**Прочие уборки**

- `StreamerTools.Style` стал git submodule по пути `src/StreamerTools.Style`
- `MainViewModel` разделён на шесть partial-классов плюс ViewModel'ы стрипов;
  `MainViewModel.Behaviour` владеет состоянием трея и автозапуска, не зная ни про `NotifyIcon`, ни про реестр
- `LocalizableViewModel` обновляется через `IDispatcherService` там, где события реально приходят не из UI-потока, — команды MIDI и док-панели OBS
- Поиск по списку установленных приложений — обычная `ObservableCollection`, пересобираемая по требованию, а не `ICollectionView` с фильтром
- Локализованные строки переехали в `SoundMeeter.ChangeLanguage` и сливаются в `Application.Resources` через `LocResources`
- Добавлены `README.md` / `README.ru.md` со схемой аудиосигнала и нетривиальными ограничениями NAudio

### Тесты

120 тестов xUnit в шести проектах, ни один из которых не требует аудиожелеза:

| Проект | Тестов | Что покрывает |
|---|---|---|
| `SoundMeeter.App.Tests` | 54 | WPF-представления и элементы управления (STA-хост), сброс параметров, попапы эффектов, кнопки FUNC, значки приложений, границы модулей и слоёв |
| `SoundMeeter.Audio.Tests` | 31 | Блоки DSP, кольцевой буфер, мигратор настроек, проверка независимости от WPF |
| `SoundMeeter.Update.Tests` | 18 | Сравнение семантических версий и выбор файла релиза |
| `SoundMeeter.Core.Tests` | 11 | ViewModel'ы, MIDI-привязки, восстановление пресета через движок, «ядро остаётся без WPF» |
| `SoundMeeter.ChangeLanguage.Tests` | 6 | Разрешается каждый ключ локализации; каждый языковой файл полон |

xUnit v2 не умеет поднимать STA-поток, поэтому UI-тесты идут через
`SoundMeeter.App.Tests/Infrastructure/UiHost.cs`, который создаёт один STA-поток с настоящим `Application` и установленной локализацией.

---

## Known gaps

Tracked in `README.md`; unchanged by this delta.

- No `LICENSE` file — must be added before the repository is meaningfully published
- No CI — tests are run manually only
- No user manual — usage is undocumented beyond the README
- Version inconsistency: `SoundMeeter.App.csproj` says `0.0.1`, `app.manifest` hardcodes  `1.0.0.0`. The manifest is embedded as-is by the SDK, so do **not** try to inject an
  MSBuild property into it — Windows will refuse to start the executable
- `IAudioEngine` is a 25-member interface; splitting it into `IRoutingTable`,
  `IDeviceCatalog`, `IRStripTopology` and `IPresetStore` is a known refactor
- `Loc` and `AppLog` are static, with ~99 call sites across ViewModels, which limits
  ViewModel testability
- Auto-update is hard-wired to `IDerkBot/SoundMeeter` as compile-time constants