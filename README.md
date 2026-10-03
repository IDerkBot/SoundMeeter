# SoundMeeter

An open-source Windows audio mixer for streamers and podcasters — a VoiceMeeter-style
"hardware mixer" in software.

SoundMeeter captures audio from several sources at once (microphones, WASAPI loopback
of playback devices, halves of virtual audio cables), routes each one independently into
one or more output buses, applies per-strip effects, and meters the result live. It can be
driven from a MIDI control surface and from a dock panel inside OBS Studio.

```
mic ──┐                                    ┌─► strip 1 ─┐
      ├─► Denoiser ─► Compressor ─► Gain ──┤            ├─► Bus 1 ─► headphones
loop ─┤             ─► Delay ─► Reverb ────┤            │
      │                                   └─► strip 2 ─┘
cable ┘                                            └─► Bus 2 ─► stream
```

**[Русский](README.ru.md)**

---

## Features

**Mixing**

- Input strips and output buses as separate, reorderable columns
- Per-strip vertical fader, segmented VU meter, dB readout
- LED buttons: **Mute**, **Solo**, **Mono** (solo is global-aware across inputs and buses)
- Per-strip routing to hardware outputs (`OUT`) and virtual-cable inputs (`VIRT`), both multi-select
- Two configurable **FUNC** buttons per input strip for instant routing presets
- Live MIDI control: CC, Note and PitchWheel bindings with Toggle / Hold / Latch modes

**DSP (per input strip)**

- Gain
- Compressor — threshold, ratio, attack, release, makeup gain
- Delay — time, feedback, damping, mix
- Reverb — size, damping, mix
- RNNoise denoiser — noise-removal percentage, dry/wet, 4 formant EQ bands

**Routing & integration**

- Per-application routing: drag a running app onto a strip to capture only that app
- Installed-apps list read from the registry, with a keyword filter for system noise
- Virtual-cable auto-pairing (VB-Cable, VAC, StreamerCable) — matches the two halves of a
  cable by name and refuses to bind a cable output as a strip input
- **OBS Studio dock panel** — a built-in loopback HTTP + WebSocket server pushes strip
  state to a Browser Dock at 30 Hz and accepts volume/mute/solo/mono commands back
- Auto-update from GitHub Releases (portable ZIP, self-replacing via `robocopy /MIR`)

**Application**

- System tray icon, optional minimize-to-tray
- Run at startup with administrator rights (scheduled task, no UAC prompt at logon) and single-instance guard
- English and Russian UI
- Log viewer with a rotating file log

---

## Requirements

| | |
|---|---|
| OS | Windows 10 / 11, **x64** only |
| SDK | .NET 10 SDK with the Windows Desktop workload (`net10.0-windows`) |
| Runtime | .NET 10 Desktop Runtime |
| Privileges | **Administrator** — enforced by `app.manifest` (`requireAdministrator`) |
| Optional | A virtual audio cable (VB-Cable / VAC / StreamerCable), OBS Studio, a MIDI control surface |

Windows-only by design: the project targets `net10.0-windows` / `win-x64` throughout and
depends on WASAPI, WinRT/COM audio policy, the registry and WinForms. There is no
cross-platform path and no `#if` platform guards.

---

## Building

```powershell
# from the repository root
dotnet build src\SoundMeeter.slnx -c Release

# or just the application
dotnet build src\SoundMeeter.App\SoundMeeter.App.csproj -c Release
```

The solution uses the new XML `.slnx` solution format, so it needs a recent SDK
(developed against 10.0.401).

The shared style library `StreamerTools.Style` is a git submodule at `src/StreamerTools.Style`.
Initialize it before the first build:

```powershell
git submodule update --init
```

### Run

```powershell
dotnet run --project src\SoundMeeter.App\SoundMeeter.App.csproj
```

or launch the produced executable directly (it will prompt for elevation):

```
src\SoundMeeter.App\bin\Release\net10.0-windows\win-x64\SoundMeeter.exe
```

The project is called `SoundMeeter.App`, but the assembly and the executable are named
`SoundMeeter`: the executable name is part of the release contract (the updater looks for
`SoundMeeter.exe` inside the portable ZIP and restarts the process by that name), so
renaming it is a separate task that has to start from `UpdateApplier`.

---

## Tests

```powershell
dotnet test src\SoundMeeter.slnx
```

120 xUnit tests across six projects, no audio hardware required:

| Project | Tests | Covers |
|---|---|---|
| `SoundMeeter.App.Tests` | 54 | WPF views and controls (STA host), param reset, effect popups, app icons, module and layer boundaries |
| `SoundMeeter.Core.Tests` | 11 | ViewModels, MIDI bindings, preset restore through the engine, "core stays WPF-free" |
| `SoundMeeter.Audio.Tests` | 31 | DSP blocks, ring buffer, settings migrator, WPF-free boundary checks |
| `SoundMeeter.Update.Tests` | 18 | Semantic version comparison and release-asset selection |
| `SoundMeeter.ChangeLanguage.Tests` | 6 | Every localization key resolves; every language file is complete |

xUnit v2 cannot host an STA thread, so UI tests go through
`SoundMeeter.App.Tests/Infrastructure/UiHost.cs`, which spins up a single STA thread with
a real `Application` and localization installed.

There is no CI pipeline — tests are run manually. That is tracked as a known gap.

---

## Project layout

```
src/
├── SoundMeeter.App/              WPF shell (WinExe, assembly "SoundMeeter")
│   ├── App.xaml.cs               composition root, startup sequence
│   ├── Views/                    MainView decomposed into UserControls + windows
│   ├── Views/Controls/           per-strip views, MixerTheme resource dictionary
│   ├── Controls/                 SegmentedMeter, GainKnob, HorizontalFillPanel
│   ├── Converters/               value converters
│   ├── Services/LocResources.cs  merges localized strings into Application.Resources
│   └── Resources/                application icon
├── SoundMeeter.Core/             ViewModels + services (logic layer, no WPF)
│   ├── ViewModels/               MainViewModel split into 6 partials + per-strip VMs
│   ├── Services/                 audio engine, settings, MIDI, OBS dock server
│   ├── AudioPolicy/              undocumented AudioPolicyConfig interop
│   └── Resources/                app filter (copied to output), embedded obs-dock assets
├── SoundMeeter.Audio/            audio core + models (WPF-free)
│   ├── Audio/                    capture, ring buffer, taps, DSP chain, RNNoise
│   └── Models/                   settings schema, migrator (v0 → v5)
├── SoundMeeter.Logger/           rotating file logger
├── SoundMeeter.ChangeLanguage/   EN/RU strings (Loc)
├── SoundMeeter.Update/           GitHub Releases auto-update
├── SoundMeeter.StartUp/          run-at-startup + single instance
├── SoundMeeter.TrayIcon/         NotifyIcon
└── *.Tests/                      six test projects
```

### Module rules

Dependencies point one way only, and this is enforced by tests rather than convention
(`ModuleBoundaryTests`, `AudioModuleBoundaryTests`, `CoreWpfFreeTests` inspect
`Assembly.GetReferencedAssemblies()`):

- No feature module references another feature module
- No module references the core or the application
- `SoundMeeter.Logger` and `SoundMeeter.ChangeLanguage` are the base layer and reference nothing of their own
- The core does not reference the application, and the application references the core
- The core does not reference the UI modules (`SoundMeeter.TrayIcon`, `StreamerTools.Style`)
- **Neither the core nor `SoundMeeter.Audio` may reach WPF** — `PresentationFramework`,
  `PresentationCore`, `WindowsBase` and `System.Xaml` are rejected even transitively
  (`CoreWpfFreeTests`, `AudioModuleBoundaryTests`)

```
              ┌──────────────────────────┐
              │ SoundMeeter.App (WPF)    │  WinExe, assembly "SoundMeeter"
              └───────┬──────────┬───────┘
                      ▼          │
              ┌──────────────────────────┐
              │     SoundMeeter.Core    │  ViewModels + services
              └────────┬─────────────────┘
      ┌─────────┬───────┼────────┬──────────┬───────────┐
      ▼         ▼       ▼        ▼          ▼           ▼
   Audio     Logger  ChangeLang Update   StartUp     TrayIcon ← App only
      └──────►  └──────┴─────┴──────────┘
                  (base — no outbound edges)
```

**The core is WPF-free.** `SoundMeeter.Core` does not set `UseWPF`, and
`CoreWpfFreeTests` rejects `PresentationFramework`, `PresentationCore`, `WindowsBase` and
`System.Xaml` anywhere in its reference graph. The core test project therefore runs
without a UI thread, without an `Application` and without a window.

Getting there meant pushing five WPF dependencies behind interfaces:

| Was in the core | Now |
|---|---|
| `BitmapImage`/`ImageSource` icons, `SHGetFileInfo` P/Invoke duplicated in three ViewModels | `FileIconConverter` / `ProcessIconConverter` + one `ShellIcons` copy on the UI side; the core exposes only a path or a PID |
| `IAudioService.GetAppIcon(uint) → BitmapImage` (never called by anything) | deleted, together with its icon cache and `System.Drawing` |
| `Application.Current.Dispatcher` in `LocalizedViewModel` | `IDispatcherService` (`Post` + `HasThreadAccess`), used where events really come from a background thread — MIDI and OBS dock commands |
| `DispatcherTimer` for the meters | `IUiTimer`, implemented by `DispatcherTimerAdapter` in the application |
| `ICollectionView` with a `Filter` for the installed-apps search | a plain `ObservableCollection` rebuilt on demand |
| `Clipboard.SetText` in two ViewModels | `IClipboardService` |

Namespaces stayed the same (`SoundMeeter.ViewModels`, `SoundMeeter.Services`,
`SoundMeeter.Views.Controls`): the split moves assemblies, not namespaces, so no `using`
and no XAML `clr-namespace` had to change.

---

## Audio signal path

```
InputSource (WasapiCapture | WasapiLoopbackCapture)
  → decode PCM16/24/32/float → downmix to stereo 48 kHz → optional mono fold
  → DenoiserDsp (RNNoise) → StripDsp (compressor → gain → delay → reverb)
  → SampleRingBuffer.Write()                     [1 s ring, 48 kHz × 2 ch]
        │
        ├─ RingCursor → BusTap (per input × bus) : ISampleProvider
        │                 volume = muted ? 0 : DbToLinear(input.VolumeDb)
        │                 solo resolved through shared SoloState
        │
        └─ RingCursor → ...
        ▼
  BusDsp : ISampleProvider (wraps MixingSampleProvider)
        ▼
  WasapiOut (Shared, event-driven, ~100 ms latency) → physical device
```

Non-obvious constraints worth knowing before changing the engine:

- **`BusTap.Read` must always return `buffer.Length`.** NAudio's `MixingSampleProvider`
  silently *removes* any source that returns fewer samples than requested, so a short read
  makes a strip vanish from the mix rather than just going quiet.
- **Never loopback-capture the engine's own output** — instant feedback loop.
- `WasapiOut.Init` needs an explicit `new SampleToWaveProvider(dsp)`; NAudio 3.1.0 has no
  implicit conversion.
- `MMDevice` instances must be explicitly disposed in `RefreshDevices` or COM leaks accumulate.
- Per-route `GainDb` is *not* applied in the audio path — strip volume is the only gain source.

---

## Configuration

All configuration is on disk; no environment variables are read anywhere in the codebase.

| What | Where |
|---|---|
| Settings | `%APPDATA%\SoundMeeter\settings.json` |
| Logs | `%LOCALAPPDATA%\SoundMeeter\logs\` (rotating) |
| Run at startup | Scheduled task `\SoundMeeter` (logon trigger, `RunLevel=Highest`; a leftover `HKCU\...\Run` record is migrated to it) |
| OBS dock registration | `%APPDATA%\obs-studio\user.ini` → `[BasicWindow] ExtraBrowserDocks` |
| App filter keywords | `Resources/system_apps_filter.json` (copied to output) |
| Single instance | `Local\SoundMeeter.SingleInstance.<user>` mutex + show-window event |

Notable `settings.json` behaviour:

- Written **atomically** — `settings.json.tmp` + `File.Replace`, then re-read to verify
- A corrupt file is **quarantined** to `settings.corrupt.json`, never silently deleted
- Schema version is tracked (`AppSettings.SchemaVersion`, currently **5**) and migrated
  through a `MigrateStep` chain; a file from a *newer* build is never overwritten, so a
  downgrade cannot destroy your settings
- Auto-save runs on a 2 s timer, driven by a dirty flag, off the UI thread

### OBS dock

Enabled from the toolbar's Settings button. The app starts a loopback-only HTTP + WebSocket
server (default port **17954**) and upserts a Browser Dock entry into OBS `user.ini` under a
fixed UUID. It refuses to edit the file while OBS is running or if the INI is malformed, and
writes a `user.ini.soundmeeter.bak` backup first.

The server is hand-rolled on `TcpListener`: `HttpListener` cannot upgrade to WebSocket, and
Kestrel would break single-file publish. Assets are embedded resources, and the panel picks
up the UI language through an injected `window.SM_I18N`.

---

## Tech stack

C# / .NET 10, WPF, `CommunityToolkit.Mvvm` source generators, NAudio 3.1.0, RNNoise via
P/Invoke, `Microsoft.Extensions.DependencyInjection`, `System.Text.Json`.

| Package | Version | Role |
|---|---|---|
| `NAudio` | 3.1.0 | WASAPI capture / loopback / output, MMDevice, MIDI |
| `CommunityToolkit.Mvvm` | 8.4.2 | `[ObservableProperty]`, `[RelayCommand]` |
| `Microsoft.Extensions.DependencyInjection` | 10.0.12 | composition root |
| `Microsoft.Extensions.Logging(.Abstractions)` | 10.0.11 | `ILogger` |
| `YellowDogMan.RRNoise.NET` | 0.1.9 | ships native `rnnoise.dll` |
| `xunit` / `xunit.runner.visualstudio` | 2.5.3 | tests |

RNNoise is called through a hand-written P/Invoke (`RnNoiseInterop`) rather than the
package's managed `Denoiser` wrapper, because that wrapper hides its internal ring buffer
and so cannot keep the dry and denoised signals time-aligned.

---

## Other known issues

- **No LICENSE file.** This must be added before the repository is meaningfully published.
- **No CI.** Tests are manual only.
- **No user manual.** Usage is undocumented beyond this file.
- **Version inconsistency.** `SoundMeeter.App.csproj` says `0.0.1`, `app.manifest` hardcodes
  `1.0.0.0`, and the notes say `1.0.0` was intended. The manifest is embedded as-is by the
  SDK, so do *not* try to inject an MSBuild property into it — Windows will refuse to start
  the executable.
- **`IAudioEngine` is a 25-member interface.** Splitting it into `IRoutingTable`,
  `IDeviceCatalog`, `IRStripTopology` and `IPresetStore` is a known refactor.
- **`Loc` and `AppLog` are static**, with ~99 call sites across ViewModels, which limits
  ViewModel testability.
- **Auto-update is hard-wired** to `IDerkBot/SoundMeeter` as compile-time constants, and
  depends on a GitHub Release having been published.
- **Icon loading is untestable at the ViewModel level** — and no longer needs to be:
  icons live in the UI converters (`AppIconTests` covers them), while the core only knows
  paths and PIDs.

`Obsidian/StreamerTools/` holds the author's working notes (architecture map, a
`LAST_ACTION` log, backlog, and a NAudio gotchas list) in Russian. Useful, but not
maintained as user documentation.

---

## License

**To be determined.** No `LICENSE` file exists yet. Third-party dependencies are consumed
under their own licenses (NAudio — MIT, RNNoise — BSD-3-Clause, CommunityToolkit.Mvvm — MIT).
