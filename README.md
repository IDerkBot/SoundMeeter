# SoundMeeter

An open-source Windows audio mixer for streamers and podcasters — a VoiceMeeter-style
"hardware mixer" in software.

SoundMeeter captures audio from several sources at once (microphones, WASAPI loopback
of playback devices, halves of virtual audio cables), routes each one independently into
one or more output buses, applies per-strip effects, and meters the result live. It can be
driven from a MIDI control surface and from a dock panel inside OBS Studio.

```
mic ──┐                                    ┌─► strip 1 ─┐
      ├─► Denoiser ─► EQ ─► Compressor ─► Gain ──┤            ├─► Bus 1 ─► headphones
loop ─┤             ─► Delay ─► Reverb ────┤            │
      │                                   └─► strip 2 ─┘
cable ┘                                            └─► Bus 2 ─► stream
```

**[Русский](README.ru.md)**

---

## Features

**Mixing**

- Input strips and output buses as separate, reorderable columns
- The mixer starts **empty**: strips are added by you (`＋`) and are never created
  automatically from the list of sound devices
- Per-strip vertical fader, segmented VU meter, dB readout
- LED buttons: **Mute**, **Solo**, **Mono** (solo is global-aware across inputs and buses)
- Per-strip routing to hardware outputs (`OUT`) and virtual-cable inputs (`VIRT`), both multi-select
- Two configurable **FUNC** buttons per input strip for instant routing presets
- Live MIDI control: CC, Note and PitchWheel bindings with Toggle / Hold / Latch modes

**DSP (per input strip)**

- Gain
- Graphic equalizer — 10 bands (31 Hz…16 kHz) with a draggable response curve in its
  own window, 5 curve presets, low cut, high cut and overall makeup gain
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
- Auto-update from GitHub Releases (portable ZIP, self-replacing via `robocopy /MIR`), with the
  release notes shown as rendered markdown — headings, lists, links, code

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

### Publish (single file)

The release artifact is **`SoundMeeter.exe` plus one editable file next to it**
(`Resources/system_apps_filter.json`) - no DLLs, no `.json` next to the exe beyond that, and
no .NET runtime requirement:

```powershell
dotnet publish src\SoundMeeter.App\SoundMeeter.App.csproj -p:PublishProfile=portable -c Release -o publish
```

The profile lives in `src/SoundMeeter.App/Properties/publishProfiles/portable.pubxml`. It is a
profile rather than csproj properties on purpose: those only mean anything during publish, and
in the csproj they would also apply to `dotnet build`, making every build self-contained.

| Property | Why |
|---|---|
| `SelfContained` | a release recipient may not have the .NET Desktop Runtime installed |
| `IncludeNativeLibrariesForSelfExtract` | the only native library (`rnnoise.dll`) travels inside the exe and is extracted on first start; without this the SDK drops native files next to the exe |
| `EnableCompressionInSingleFile` | ~69 MB instead of ~190 MB; extraction is paid once, then cached by content hash |
| `DebugType=none`, `AllowedReferenceRelatedFileExtensions=none` | otherwise `.pdb` files land next to the exe, and it is no longer one file |
| `SatelliteResourceLanguages=en;ru` | only the interface languages travel inside the exe |

`PublishTrimmed` is **not** enabled and must never be: trimming breaks WPF reflection.

Two things this format changed in the code:

- `Resources/system_apps_filter.json` ships next to the exe again, because it is meant to be
  edited: the system-app keyword list is per machine. The same file is also embedded in
  `SoundMeeter.Core`, purely as the source for creating it if it is missing (first run, a
  deleted file, a read-only install directory) - the file always wins over the embedded copy,
  and the update never deletes it.
- `UpdateApplier` accepted only archives containing `SoundMeeter.dll`, so **every** single-file
  update would have been rejected as "incomplete". A payload of exactly one executable of at
  least 8 MB is now accepted as a single-file build; anything smaller is still rejected, so a
  truncated archive cannot slip through.

The deletion rule became narrower on purpose: an update removes **only `.dll` and `.pdb` files**
anywhere under the install directory - the leftovers of the old multi-file build, including its
stale `ru\*.resources.dll`, which would otherwise shadow the localization baked into the new exe.
Folders are never removed (they just end up empty), and nothing else next to the exe is touched:
not `.json`, not logs, not anything the user put there.

`dotnet publish` without `-p:PublishProfile=portable` still works but warns (`SM0001`): it
produces the old multi-file layout, which is fine for debugging and wrong for a release.

### Release script

`publish.ps1` in the repository root runs the whole release flow - tests, publish, checks, zip:

```powershell
.\publish.ps1                      # artifacts\SoundMeeter.zip, tests included
.\publish.ps1 -SkipTests           # skip the test run
.\publish.ps1 -OutputPath D:\rel   # put the artifacts somewhere else
```

Layout: the loose files go to `artifacts\publish\`, the archive to `artifacts\SoundMeeter.zip`.
The archive must not live *inside* the publish directory - it would end up inside itself.

Each check exists because the failure it catches is silent otherwise:

- **tests run first** - the updater already installs versions on its own, so a release built
  without them is a knowingly broken release;
- **the artifacts directory is wiped before publishing** - `dotnet publish` does not remove
  files it no longer produces, and those leftovers are exactly what would go into the zip;
- **no `.dll`/`.pdb` next to the exe, and the exe is at least 8 MB** - otherwise this is not the
  single-file build that `UpdateApplier` accepts;
- **`SoundMeeter.exe` at the archive root** (`./SoundMeeter.exe` is not found by the updater) and
  the archive must not contain itself.

On success it prints the archive path, its size, the file list and a SHA-256. Compression uses
the system `tar` (bsdtar ships with Windows 10+) because the exe is already compressed inside the
bundle; `Compress-Archive` is the fallback. The script is saved as UTF-8 **with** BOM - Windows
PowerShell 5.1 reads BOM-less `.ps1` as ANSI and breaks the parser on non-ASCII.

---

## Tests

```powershell
dotnet test src\SoundMeeter.slnx
```

271 xUnit tests across six projects, no audio hardware required:

| Project | Tests | Covers |
|---|---|---|
| `SoundMeeter.App.Tests` | 102 | WPF views and controls (STA host), param reset, effect popups, empty-mixer hints, markdown release notes, app icons, module and layer boundaries |
| `SoundMeeter.Core.Tests` | 32 | ViewModels, MIDI bindings, preset restore through the engine, settings import/export, changelog text, app-filter file and its embedded fallback, "core stays WPF-free" |
| `SoundMeeter.Audio.Tests` | 95 | DSP blocks, ring buffer, settings migrator, WPF-free boundary checks |
| `SoundMeeter.Update.Tests` | 36 | Semantic version comparison, release-asset selection, multi-version changelog, update plan for single-file payloads |
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
InputSource (WasapiRecorder: loopback capture | mic capture)
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
  WasapiPlayer (Shared, event-driven, MMCSS) → physical device
```

Latency is set by the two WASAPI endpoint buffers. The budgets live in
`AudioEngineDefaults` (`InputBufferMilliseconds` 20, `OutputBufferMilliseconds` 25, plus
an IAudioClient3 low-latency request on the output with a soft fallback). NAudio reports
only what the driver actually granted, so every strip and bus logs its effective latency
**once, when it opens** — `latency requested=…, actual=…, lowLatency=…`.

The ring contributes nothing *provided* it stays demand-driven: it hands out exactly what
the bus asks for and pads the rest with silence. Two rules keep it that way:

- **A new `RingCursor` starts at the ring's live edge** (`TotalWritten`), not at 0.
  Position 0 is the ring's creation, so on an already-wrapped ring it would resolve to
  `written − Capacity` — a cursor would silently replay a second of stale audio and stay a
  second behind forever.
- `RingCursor.BufferedFrames` / `SkippedFrames` exist to assert that in tests; there is
  deliberately no periodic latency logging, it floods the log at Info level.

Non-obvious constraints worth knowing before changing the engine:

- **`BusTap.Read` must always return `buffer.Length`.** NAudio's `MixingSampleProvider`
  silently *removes* any source that returns fewer samples than requested, so a short read
  makes a strip vanish from the mix rather than just going quiet.
- **Never loopback-capture the engine's own output** — instant feedback loop.
- `WasapiPlayer.Init` needs an explicit `new SampleToWaveProvider(dsp)`; NAudio 3.1.0 has no
  implicit conversion.
- **A silent capture packet must still be run through the DSP chain.** The whole point of a
  `Silent` packet is that the pipeline advances over zeros; skipping it freezes the delay /
  reverb tails and the compressor envelope until the next non-empty packet.
- `MMDevice` instances must be explicitly disposed in `RefreshDevices` or COM leaks accumulate.
  The one exception is the device a `WasapiPlayer` is built from: it stays owned by that bus
  until it closes.
- Per-route `GainDb` is applied in the audio path by `BusTap` (read live from `BusRouting`),
  multiplied into the strip volume.

---

## Configuration

All configuration is on disk; no environment variables are read anywhere in the codebase.

| What | Where |
|---|---|
| Settings | `%APPDATA%\SoundMeeter\settings.json` |
| Logs | `%LOCALAPPDATA%\SoundMeeter\logs\` (rotating) |
| Run at startup | Scheduled task `\SoundMeeter` (logon trigger, `RunLevel=Highest`; a leftover `HKCU\...\Run` record is migrated to it) |
| OBS dock registration | `%APPDATA%\obs-studio\user.ini` → `[BasicWindow] ExtraBrowserDocks` |
| App filter keywords | `Resources/system_apps_filter.json` next to the exe - editable by the user, and re-created from the copy embedded in `SoundMeeter.Core` if missing; updates never delete it |
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
  depends on a GitHub Release having been published. The changelog costs one extra API call
  (`/releases`) on top of `/releases/latest`; if it fails, only the target release is shown.
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
