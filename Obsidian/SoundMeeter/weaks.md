# SoundMeeter — ревью проекта, слабые места и план работ

> Дата ревью: 2026-10-02 · Ветка `main` @ `938eac7` · 14 проектов, ~9 400 строк production C#/XAML
> Сборка `Release` — 0 warnings / 0 errors. Тесты — 120 passed. Так что проблема не в том, что не компилируется.
> Проблема в том, что **compile-clean ≠ correct**.

---

## Содержание

- [[#Сводка|Сводка]] — что срочно, что потом
- [[#Блокеры-релиза-CRITICAL|Блокеры релиза (CRITICAL)]] — 5 штук (C-1, C-1a, C-1b, C-1c исправлены)
- [[#Высокий-приоритет-HIGH|Высокий приоритет (HIGH)]] — 11 штук
- [[#Средний-приоритет-MEDIUM|Средний приоритет (MEDIUM)]]
- [[#Низкий-приоритет-LOW|Низкий приоритет (LOW)]]
- [[#Архитектура-и-рефакторинг|Архитектура и рефакторинг]]
- [[#Тесты-и-CI|Тесты и CI]]
- [[#Документация|Документация]]
- [[#Что-сделано-хорошо|Что сделано хорошо]]
- [[#План-работ|План работ]] — по шагам

---

## Сводка

**Проект:** WPF-микшер аудиоустройств с перенаправлением приложений, MIDI-биндами, OBS-доком и автообновлением.
**Модулей 14, разделение на Core/App/Audio — свежее и сделано хорошо.** Границы модулей проверяются тестами, `UseWPF` вынесен из ядра осознанно, `StripDspTests` — реально сильные тесты на сигнале.

**Но три подсистемы написаны без учёта их среды выполнения:**

| Подсистема | Проблема | Где болит |
|---|---|---|
| `WasapiAudioEngine` + DSP | Код захватывающего и воспроизводящего потоков написан как обычный C# — с `lock`, аллокациями и логированием в колбэках | щелчки, цифровая тишина в денойзере, зависание UI |
| `GithubUpdateService` + `UpdateApplier` | Качает и запускает код, проверяя только первые 2 байта файла | подмена релиза = исполнение от админа |
| `ObsDockServer` | Свой HTTP/WS сервер без единой проверки `Origin`/`Host`/токена | любой сайт на машине глушит стрим |

**Ключевая мысль:** `MainViewModel.Midi.cs`, `ObsDockServer.cs`, `UpdateApplier.cs` и `WasapiAudioEngine.cs` написаны с одинаковым «обычным» стилем — но каждый из них живёт в среде, где обычный стиль не работает: realtime-поток, сетевой сокет с недоверенным входом, `requireAdministrator` + запуск скрипта.

### Что срочно / что потом

| Приоритет | Кол-во | Темы |
|---|---|---|
| 🔴 CRITICAL | 5 | краши процесса, автообновление без подлинности, док без аутентификации, потеря настроек при даунгрейде, нет LICENSE |
| 🟠 HIGH | 11 | потеря устройств без реакции, God-объекты, нет CI, обход тестов границ, утечки памяти |
| 🟡 MEDIUM | ~30 | рассинхрон диапазонов, аллокации в горячем пути, дубли XAML, недоступность |
| ⚪ LOW | ~25 | мёртвый код, копипаст, мелкие расхождения |

---

## Блокеры релиза (CRITICAL)

### C-1. ~~Денойзер вставляет цифровую тишину — до 60 % каждого пакета~~ ✅ исправлено

**Было.** `src/SoundMeeter.Audio/Audio/DenoiserDsp.cs` держал 4 кадра очереди (40 мс):

```csharp
private const int QueueFrames = 4;
private const int QueueCapacity = QueueFrames * FrameFloats;  // 4 * 960 = 1920 фреймов = 40 мс
```

Захват при этом отдаёт пакеты по **50–100 мс**: `InputSource.cs` создаёт `WasapiCapture` без параметра латентности, поэтому NAudio 3.1.0 берёт дефолт `audioBufferMillisecondsLength: 100`. Денойзеру скармливался весь пакет, тот переполнялся и **молча отбрасывал хвост**, а выход, которого не хватило, **дозаполнялся нулями**:

```csharp
if (!EnqueueSample(_inQueue, ref _inCount, ref _inHead, stereo[i * 2], stereo[i * 2 + 1]))
    break; // переполнение: вход пришёл быстрее выхода, лишнее отбрасываем
// ...
else
    l = r = 0f;
```

Арифметика:

- пакет 50 мс (2400 фреймов) → 1920 фрейма звука + **480 фреймов (10 мс) цифровой тишины** → 20 % щелчков при 20 Гц
- пакет 100 мс (4800 фреймов) → **2880 фреймов (60 мс) тишины**

> [!warning] Комментарий в коде просто неверен
> `DenoiserDsp.cs:56-57`: «4 кадра = 40 мс — с большим запасом относительно максимального размера батча WASAPI в общем режиме». В shared-режиме размер пакета ограничен **размером буфера эндпоинта (100 мс)**, а не 10 мс. Предположение в основе константы неверно.

**Стало** (замеры — до/после, тот же стенд):

| Что | Было | Стало |
|---|---|---|
| тишины в пакете 50 мс | 20 % (19 200 кадров) | 0 |
| тишины в пакете 100 мс | 60 % (57 600 кадров) | 0 |
| щелчок в пакете 100 мс (шаг ровнотона 0.0173) | 0.2859 | 0.1007 = столько же, сколько на 10-мс пакетах |
| счётчики `UnderrunFrames` / `OverrunFrames` | не существовали | есть, логируются при `Stop()` |

Очередь выросла до `MaxPacketFrames = 12000` кадров (250 мс, кратно 480), `InputSource` пишет в журнал ненулевые счётчики, а `DenoiserDspTests` бьёт по каждому размеру пакета (10/25/50/100/200 мс).

### C-1a. ~~Денойзер складывал «сухой» и «денойзерный» сигнал в противофазе~~ ✅ исправлено

Отдельный дефект, найденный при разборе C-1 и не покрытый этим ревью.

`Noise Remover` — линейный кросфейд «сухого» сигнала с результатом RNNoise. Складывать их можно только в фазе. Реальная групповая задержка `rnnoise.dll` измеряется как **960 сэмплов (20 мс, два кадра STFT)**, а код брал `HalfFrame = 240` и ограничивал подстройку диапазоном `[200, 280]`:

```csharp
private const float MinDryDelay = HalfFrame - 40f;   // 200
private const float MaxDryDelay = HalfFrame + 40f;   // 280
```

Расстройка 720 сэмплов — это **ровно половина периода 300 Гц**. Кросфейд не складывал сигналы, а вычитал их, и громкость по крутилке шла не монотонно:

| Noise Remover | Уровень ровнотона 300 Гц |
|---|---|
| 0 % | −13.5 дБ |
| 40 % | −27.3 дБ |
| **50 %** | **−60.7 дБ** |
| 60 % | −27.7 дБ |
| 100 % | −13.6 дБ |

На 50 % сигнал пропадал целиком, а по краям диапазона был в норме — то есть ровно тот «треск на середине крутилки», который и описывался как проблема. На 500 Гц провал был −64 дБ.

Исправлено: `RnNoiseDelay = FrameSize * 2` как начальное значение и границы `[RnNoiseDelay ± 96]`; «сухой» сигнал читается из кольца истории на 4 кадра (`HistoryFrames = 4`), потому что задержка больше одного кадра в пару «предыдущий + текущий» не помещается. Замер после правки: отклонение уровня от предсказания кросфейда — 0.09 дБ на всём диапазоне 0…100 %.

Регрессия закрыта тестами `DryAndWetAreAlignedInTime` (фаза, а не уровень: расстройка в 180° не «почти нулевая»),
`NoiseRemoverSweepNeverDipsBelowTheEndpoints` и `LongPacketsDoNotClick`.

### C-1b. ~~Одно `rnnoise_state` на оба канала — голос рассыпается на стерео~~ ✅ исправлено

Дефект, из-за которого на **высоких** значениях Noise Remover и Dry/Wet голос становился «роботом» и трещал.

```csharp
private IntPtr _rnState;                                                    // одно на оба канала
// ...
float prob = RnNoiseInterop.ProcessFrame(_rnState, _scL, _rnL);
RnNoiseInterop.ProcessFrame(_rnState, _scR, _rnR);                          // затирает буферы первого
```

`rnnoise_state` внутри хранит **буферы перекрытия STFT и оценку шума**. Второй вызов на том же состоянии затирает то, что первый только что записал, поэтому:

- реконструкция с перекрытием считается по буферам, в которых лежит *другой* канал;
- модель шума и VAD видят чередующийся поток L, R, L, R;
- левый и правый выходы получаются из **разных** состояний и не совпадают.

Микрофон моно (и кнопка Mono сводит L+R в центр), поэтому на входе L == R, а на выходе — два разных сигнала. Замер `RMS(L−R)` в процентах от `RMS(L)`:

| Noise Remover | было | стало |
|---|---|---|
| 0 % | 0 % | 0 % |
| 50 % | 87.7 % | 0 % |
| 100 % | **133.3 %** | 0 % |

133 % — каналы не просто разошлись, а разошлись сильнее, чем сам сигнал. Побочный эффект тот же, что у C-1a: сломанная сборка с перекрытием рвёт сигнал на границах кадров, а покадровое усиление скачет — «металлический» тембр и треск.

| метрика (речь + шум) | было | стало |
|---|---|---|
| отношение максимума второй разности к p99.9 при 100 % / 100 % | **14.8×** | 1.3× |
| то же при 100 % / 75 % | 3.0× | 1.2× |
| худший покадровый скачок усиления, 100 % | +6.0 дБ | +3.2 дБ (остаток — физическое «тишина → речь») |

Исправлено: `_rnStateL` и `_rnStateR`, по состоянию на канал; вероятность речи берётся по максимуму каналов; `Dispose` освобождает оба, а конструктор при неудаче второго `rnnoise_create` не теряет первое. Побочно выяснилось, что денойзер **заработал**: прибавка к отношению сигнал/шум на речи с шумом +7…18 дБ (замер: `Dry/Wet 100 %` даёт 20.5 дБ SNR против 2.3 дБ на сухом входе).

Тесты: `MonoInputStaysMono`, `HighSettingsDoNotClick`.

### C-1c. ~~Формантный EQ обнулял состояние шести биквадов на каждом пакете~~ ✅ исправлено

```csharp
private void SetPeak(ref BiquadFilter filter, float freq, float gainDb)
{
    filter.SetPeaking(freq, gainDb, Q, InputSource.SampleRate);
    filter.ResetState();      // <-- вот это
}
```

Комментарий утверждал, что сброс «нужен, у денойзера за ними нечего тянуть». Наоборот: `ResetState()` при ненулевом сигнале на входе — это разрыв, то есть щелчок, и не один, а **шесть за пакет**, повторяясь каждые 10…100 мс, пока крутилка движется. `UpdateEq` срабатывал один раз на пакет, то есть 10–100 щелчков в секунду на любое движение полос Low/Mid/High/Group.

Комментарий противоречил и `BiquadFilter`, где сказано «смена коэффициентов не сбрасывает состояние», и эквалайзеру стрипа, который коэффициенты плавно сглаживает.

Исправлено: `UpdateEq` только запоминает цели, `AdvanceEq` двигает их к цели раз в кадр с постоянной времени 60 мс (`EqSmoothCoef`), `SetPeak` больше не сбрасывает состояние, `makeup`-gain едет тем же коэффициентом (скачок множителя на голосе слышен не хуже обрыва фильтра). Первичная установка — сразу и точно: состояние всё равно нулевое после сброса конвейера.

Замер (речь, отношение максимума второй разности к p99.9 при движении трёх полос): было **4.7×**, стало 1.2×.

Тест: `MovingTheFormantEqDoesNotClick`.

> [!note] Что осталось
> У RNNoise собственная фаза инвертирована на 100–280 Гц (его низкополосная обработка, не кросфейд). На ровнотоне 250 Гц это даёт провал до −18 дБ в середине Noise Remover, на широкополосном сигнале — 3…4.5 дБ колебания уровня на 50–65 %. Строго это лечится фазовым эквалайзером с нелинейной фазой, что скорее хак: за линейной фазой такой кривой не существует. Не сделано.

---

### C-2. `async void` из таймера → падение процесса каждые 2 секунды

`src/SoundMeeter.Core/ViewModels/MainViewModel.cs:100-101`

```csharp
_audioService.AudioDevicesChanged += async (s, e) => await RefreshDevicesAsync();
_audioService.AppsChanged        += async (s, e) => await RefreshAppsAsync();
```

Подписчик — `System.Threading.Timer` (`AudioService.cs:34,39-40`), то есть thread pool, где `SynchronizationContext.Current == null`. Лямбда компилируется в **`async void`**, и **любое** исключение из `RefreshDevicesAsync` уходит в unhandled и **убивает процесс**.

`RefreshDevicesAsync` → `GetAudioOutputDevicesAsync` → `Task.Run` над `_deviceEnumerator.EnumerateAudioEndPoints(...)` (`AudioService.cs:45,59`) — это COM-вызов WASAPI, бросающий `COMException` на мёртвом/отключённом эндпоинте.

Тот же класс бага:
- `MainViewModel.cs:103` `_ = InitializeAsync();` — fire-and-forget из конструктора, у `InitializeAsync` есть `try/finally`, но **нет `catch`**. Исключение в конструкторе — и приложение не стартует.
- `MainViewModel.Persistence.cs:72-84` `SavePool()` — `CreateSnapshot()` **не обёрнут в try/catch** (см. C-3).
- `MainViewModel.Routing.cs:112` `_ = RefreshDevicesAsync();`

**Что делать:** заменить на `Task`-returning обработчики, которые ловят и логируют. `async void` в проекте не должно быть нигде.

---

### C-3. `CreateSnapshot()` на таймере гоняется с несинхронизированными записями из VM

`src/SoundMeeter.Core/ViewModels/MainViewModel.Persistence.cs:72-84`

```csharp
private void SavePool()
{
    var snapshot = _engine.CreateSnapshot();   // ← НЕ в try/catch
    _ = SaveAsync(snapshot);
}
```

`CreateSnapshot` (`WasapiAudioEngine.cs:589-600`) берёт `_gate` движка. Но **все записи VM → модель идут мимо `_gate`** — они на UI-потоке:

- `FuncButtonViewModel.cs:279-280`
  ```csharp
  _model.BusIds.Clear();
  _model.BusIds.AddRange(assigned);
  ```
- `InputChannelViewModel.cs:724` `while (model.FuncButtons.Count <= slot) model.FuncButtons.Add(...)`
- 26 присваиваний `Model.X = value` в `InputChannelViewModel.cs:447…517`

А клон **перебирает именно эти коллекции**, держа только `_gate`:
- `WasapiAudioEngine.cs:934` `BusIds = ... new List<string>(model.BusIds)`
- `WasapiAudioEngine.cs:895` `new Dictionary<string,bool>(source.FuncBaseRouting, ...)`

`Clear()`, гоняющееся с `new List<T>(source)` → `ArgumentOutOfRangeException` / null-элементы. Исключение из `Threading.Timer` → thread pool без обработчика → **kill процесса**.

То же для скаляров: блокировка не даёт **никакого** взаимного исключения против записей VM, поэтому снапшот может поймать наполовину применённый пресет.

**Что делать:** `_gate` должен защищать и записи из VM тоже. Минимально — `try/catch` вокруг `SavePool` как стоп-кран; правильно — единый путь мутации модели.

---

### C-4. `MainViewModel` никогда не освобождает таймер сохранения через DI-путь

`src/SoundMeeter.Core/ViewModels/MainViewModel.cs:280-285`

```csharp
protected override void DisposeCore()
{
    foreach (var input in Inputs) input.Dispose();
    foreach (var bus in Buses) bus.Dispose();
    foreach (var option in Languages) option.Dispose();
}
```

`_saveTimer.Dispose()` есть только в `Shutdown()` (`MainViewModel.cs:205`). `App.OnStartup` резолвит `MainViewModel` (`App.xaml.cs:90`), и любой следующий шаг (`Restore`, `engine.RefreshDevices`, резолв `MainWindow`, `CheckUpdatesOnStartupAsync`) может бросить исключение. Тогда `OnExit` диспозит контейнер (`App.xaml.cs:122`) → вызывается `DisposeCore()` → **`_saveTimer` с периодом 2 с и захватом `this` живёт вечно**, продолжая писать `settings.json` и удерживать весь граф VM.

`Shutdown()` вызывает только `MainWindow.Closed` (`MainWindow.xaml.cs:46-51`), а он не сработает, если старт упал.

**Что делать:** `_saveTimer.Dispose()` — в `DisposeCore()`, не в `Shutdown()`.

---

### C-5. Нет восстановления при потере устройства — полоса молча умирает

В `WasapiAudioEngine` **нет ни одной подписки** на `WasapiOut.PlaybackStopped` и `WasapiOut`/`WasapiCapture` соответственно.

- **Выдернули наушники** → `PlayThread` бросает `AUDCLNT_E_DEVICE_INVALIDATED` → NAudio глушит в `PlaybackStopped` → никто не слушает. `_openBuses[busId]` (`WasapiAudioEngine.cs:701-702`) держит мёртвый `WasapiOut`, `IsAvailable` остаётся `true` (`:703`). Полоса навсегда беззвучна, **ни одной строки в логе**.
- **Выдернули микрофон** → то же самое. `InputSource.IsRunning` возвращает `true` (`:80`), `RingCursor.Read` вечно отдаёт 0.

Восстановление есть только если пользователь нажмёт «Refresh Devices» вручную (`MainViewModel.cs:161`). Таймер `AudioService` каждые 2 с обновляет только список приложений, **не каталог движка**.

**Что делать:**
- Подписаться на `PlaybackStopped` / `RecordingStopped`
- Зарегистрировать `MMDeviceEnumerator.RegisterEndpointNotificationCallback` (hot-plug)
- Один путь `ReopenBus/ReopenSource` с ограниченным числом попыток

---

### C-6. Утечка `MMDevice` / `AudioClient` на каждой неудаче открытия шины

`src/SoundMeeter.Core/Services/WasapiAudioEngine.cs:693-699`

```csharp
var device = enumerator.GetDevice(bus.DeviceId);                            // :693 — без using
var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);  // :697
output.Init(new SampleToWaveProvider(dsp));                                 // :698 — бросает
output.Play();                                                             // :699
```

`catch` на `:707-715` только логирует и ставит `IsAvailable = false`. Ни `device`, ни `output` не диспозятся. У `MMDevice` в NAudio 3.1.0 **нет финализатора** — единственный путь это `Dispose()`, на чём настаивают собственные комментарии проекта (`:66-67`).

Утечка `IMMDevice` + `IAudioClient` + аудиосессии + 2 kernel handle на каждую неудачную шину на каждую кнопку Refresh. А `Init` — это **обычный** путь отказа (см. C-7), то есть не уголок, а норма.

**Что делать:** `try/finally` с диспозом `output` и `device` на любом неуспехе.

---

### C-7. Запрос 2 каналов к не-2-канальному эндпоинту роняет `Initialize`

Оба конца жёстко просят 48000/2/float (`InputSource.cs:20,109,121`, `BusDsp.cs:21`). NAudio ставит `AutoConvertPcm | SrcDefaultQuality` — это покрывает **частоту и битность, но не число каналов**.

- 1-канальный эндпоинт (многие USB-мики в аудиодвижке отдают mono)
- 5.1 / 7.1 выход (HDMI/Realtek — очень часто)

→ `AUDCLNT_E_UNSUPPORTED_FORMAT` → `Initialize` падает → полоса мертва + утечка по C-6.

**Что делать:** `audioClient.IsFormatSupported(Shared, requested, out closest)` перед `Init` и адаптация формата.

---

### C-8. Автообновление не проверяет подлинность — только форму файла

Цепочка: `GithubUpdateService.CheckAsync` → `UpdateViewModel.InstallAsync` → `UpdateApplier.Plan` → `.ps1` → `robocopy` → `Start-Process`.

**Проверка «целостности» — это два байта и размер файла** (`UpdateApplier.cs:131-135`):

```csharp
if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
    throw new InvalidOperationException(...);
```

Нет:
- ❌ проверки SHA-256 (в дереве нет ни одного упоминания checksum)
- ❌ проверки хеша подписи по содержимому файла — `X509Certificate.CreateFromSignedFile` (`AuthenticodeVerifier.cs:60`) только **извлекает** блоб, `chain.Build` (`AuthenticodeVerifier.cs:80`) валидирует **сертификат**, а не подпись над файлом. Атакующий может подсунуть любой PKCS#7 от легитимно подписанного бинаря и получить `SignatureState.Valid`.
- ❌ пина издателя (thumbprint) — `Valid` возвращается для цепочки к **любому** доверенному корню, `signer.Subject` только печатается в диалоге
- ❌ проверки URL ассета — `GithubUpdateService.cs:169-172,198-201` берёт `browser_download_url` из JSON **как есть**. HTTPS пиннится только на метаданных (`ApiBase`, `:30`); `http://` в поле `browser_download_url` будет скачан спокойно
- ❌ проверки хотя бы `SoundMeeter.dll` — а он в single-file публикации **отсутствует** (`UpdateApplier.cs:83-87` vs `SoundMeeter.App.csproj:46-49`). То есть либо каждое обновление отвергается, либо сборка не single-file и проверка ничего не доказывает

**И fail-open** (`AuthenticodeVerifier.cs:64-71`):

```csharp
catch (CryptographicException)
{ return (SignatureState.Unsigned, "", Loc.Get("Sm.Signature.UnsignedNone")); }
catch (Exception ex)
{ return (SignatureState.Unsigned, "", ...); }
```

«Не смог прочитать подпись» → `Unsigned` → `UpdateApplier.cs:153-158` на Unsigned **только предупреждает**. Обрезанный PE, странная раскладка сертификатной таблицы, любой неожиданный exception — **обновление проходит**.

При этом UI **активно вводит в заблуждение**: `Sm.Update.Plan.PeOk` = `"{0}: PE header is fine, {1}"`.

> [!danger] Связка H-2 + H-3 из раздела ниже
> Приложение — `requireAdministrator`. Скрипт обновления пишется в **%TEMP% пользователя** (доступен medium-integrity процессам) и запускается по **bare name** `powershell.exe`. Это связка «admin исполняет скрипт из пользовательской writable-директории, разрешённый по имени» — классический local privilege escalation.

**Что делать (три правки меняют всё):**
1. Публиковать и **проверять SHA-256** ассета; принимать только `https://github.com/…`
2. Подписывать сборки и проверять **все** файлы payload через `WinVerifyTrust` с пином thumbprint; любая ошибка = `Invalid`, не `Unsigned`
3. Стейджить в соседний каталог и свопать атомарно с бэкапом; для запуска — абсолютный путь к `powershell.exe`

---

### C-9. Док OBS: любой сайт на машине управляет микшером

WebSocket **не подчиняется same-origin политике**. `new WebSocket("ws://127.0.0.1:17954/ws")` с `https://evil.example` **работает и двунаправлен**.

Проверок в `ObsDockServer.cs` — **ноль**: ни токена, ни `Origin`, ни `Host`. `Origin` парсится (`ObsDockServer.cs:462`) и **не читается**. Единственная проверка на апгрейд — непустой `sec-websocket-key` (`:264`).

Порт фиксированный (`ObsDockSettings.cs:33` `DefaultPort = 17954`) — угадывать нечего.

```js
// одна строка на любом сайте
const ws = new WebSocket('ws://127.0.0.1:17954/ws');
ws.onopen = () => ws.send(JSON.stringify({op:'mute', id:'<id>', kind:'out', v:true}));
```

| Команда | Последствие |
|---|---|
| `{op:'mute', kind:'out'}` | **глушит стрим** прямо в эфире |
| `{op:'vol', v:12}` на мик | фидбек в эфир |
| `{op:'vol', v:-60}` на шину | чёрный звук на стриме |

Усугубляет:
- `MainViewModel.Persistence.cs:46` — `if (DockSettings.Enabled) StartDock();` при каждом запуске, **атакующая поверхность переживает перезагрузки**
- `ApplyDockCommand` (`MainViewModel.ObsDock.cs:210-239`) сканирует **все** `Inputs`/`Buses` без проверки `DockSettings.Channels` — **скрытые от дока каналы тоже управляются**

Плюс **DNS rebinding** даёт чтение `/api/state` (`ObsDockServer.cs:235-236`) — имена полос, имена устройств, WASAPI endpoint ID, громкости.

> [!note] Что сделано правильно
> `IPAddress.Loopback` (`:76`) — только `127.0.0.1`, не `0.0.0.0`. LAN-атака невозможна. Но loopback **не аутентифицирует**: любой локальный процесс любого пользователя получает полный контроль над микшером админа.

**Что делать (одна правка закрывает C-9 + rebinding + локального пользователя):**
- 128-битный токен при `Start()`, вшитый в URL как `?t=…`, обязателен на `/ws`
- allowlist `Origin` (для CEF — пусто/отсутствует)
- `Host` обязан быть `127.0.0.1:<port>` или `localhost:<port>`

---

## Высокий приоритет (HIGH)

### H-1. Нет `LICENSE` на репозитории, который называет себя open source

`README.md:3` — «An open-source Windows audio mixer», remote `github.com/IDerkBot/SoundMeeter`. `git ls-files` — **ни `LICENSE`, ни `LICENSE.md`, ни `CONTRIBUTING.md`, ни `CHANGELOG.md`**. `README.md:334` пишет «**To be determined.**»

Проект тянет NAudio (MIT) + RNNoise (BSD-3) + CommunityToolkit.Mvvm (MIT). Без собственной лицензии это юридический блокер, а не мелочь. **Добавить файл — 5 минут.**

### H-2. Нет CI вообще

Проверено `Test-Path` по всем 14 типичным именам: `.github/`, `.gitlab-ci.yml`, `azure-pipelines.yml`, `appveyor.yml`, `build.ps1`, `Makefile`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, `nuget.config`, `packages.lock.json`, `*.pubxml`.

- Тесты гоняются **только руками** (признано в `Obsidian/StreamerTools/Tests.md:69`)
- Нет пина SDK → `global.json`
- Нет центрального управления пакетами → версии xUnit продублированы 5×
- Нет lock-файлов → сборка невоспроизводима
- **Нет процесса релиза вообще.** `SoundMeeter.App.csproj:30` `<Version>0.0.1</Version>` и `app.manifest:10` `version="1.0.0.0"` правятся руками. `AppVersion.Current` читает `AssemblyInformationalVersion`, то есть обновлятор сравнивает зашитый `0.0.1` с тегами GitHub

### H-3. Тесты границ модулей обходимы — и это признано в самом репозитории

`Assembly.GetReferencedAssemblies()` **не видит неиспользуемые ссылки**. Проверено на собранных артефактах: в `SoundMeeter.App.Tests.dll` отсутствуют `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `SoundMeeter.TrayIcon`, `SoundMeeter.Update`, `StreamerTools.Style` — Roslyn их вырезает.

Добавить **неиспользуемый** `<ProjectReference Include="..\SoundMeeter.TrayIcon\…"/>` в `SoundMeeter.Core.csproj` → `ModuleBoundaryTests` остаётся **зелёным**, а `TrayIcon.dll` оказывается в выходе Core. Ровно та утечка WPF/WinForms, ради которой тесты и написаны.

> [!quote] `Obsidian/StreamerTools/Tests.md:44-47`
> «Просто добавленный `ProjectReference` без единого `using` в метаданные не появляется и тестом не ловится… Первая проба «добавил ссылку на TrayIcon» так и осталась зелёной.»

При этом `README.md:170-172` утверждает, что правила «enforced by tests rather than convention». Противоречие.

Пробелы в тех же тестах:
- `ModuleBoundaryTests.cs:78` фильтрует `name.StartsWith("SoundMeeter")` — ссылки самого `StreamerTools.Style` не обходятся
- `StreamerTools.Style` вообще не в списке `Modules` (`:23-24`) — ничего не запрещает ему ссылаться на `SoundMeeter.Core`
- `AudioModuleBoundaryTests.cs:101-105` `CoreDoesNotDependOnTheApplication` — тавтология, цикл ProjectReference не компилируется

**Что делать:** парсить `ProjectReference` / `UseWPF` из `.csproj`, а не из метаданных сборки.

### H-4. `app.manifest` теряет DPI-осознанность и `supportedOS`

`src/SoundMeeter.App/app.manifest:11-17` — это весь файл, и в нём только `requireAdministrator`.

`<ApplicationManifest>` **заменяет** (не дополняет) дефолтный манифест SDK. Пропали:
- `<dpiAwareness>PerMonitorV2</dpiAwareness>`
- `<longPathAware>`
- `<compatibility><supportedOS Id="{8e0f7a12-…}"/>`

Это критично, потому что весь UI — ручная пиксельная отрисовка, а в проекте **ноль** `SnapsToDevicePixels`, `UseLayoutRounding`, `RenderOptions`, `TextOptions`:
- `SegmentedMeter.cs:62-64` — `cellW = (w - gap*(cols-1))/cols` с `gap = 1.0` → нецелые границы сегментов мылятся на 125/150 %
- `HorizontalFillPanel.cs:236-238` — 3-пиксельный индикатор дропа на дробном `x` без снапинга
- `GainKnob.cs:57,120` — `new Pen(..., 2)`, `radius = side/2 - TrackThickness/2 - 1`, без снапинга

Без `<supportedOS>` Windows применяет шимы совместимости до 8.1.

**Что делать:** добавить `<windowsSettings>` с `dpiAwareness=PerMonitorV2` + `longPathAware=true` и блок `<compatibility>` с GUID Win10.

### H-5. Нет `DispatcherUnhandledException` — нигде в продукте

Ноль вхождений в `src/` (не считая тест-хоста `UiHost.cs:47`).

- `App.OnStartup` — `async void` (`App.xaml.cs:19`). Первый `await` на `:80`. **Всё, что бросит после этого, необрабатываемо** WPF-обработкой старта: уходит в диспетчер, убивает процесс **до показа окна**, без строки в логе (лог сбрасывается только в `AppLog.Shutdown()`, `App.xaml.cs:131`)
- **9 конвертеров кидают в `ConvertBack`**: `NotImplementedException` (`BoolToOpacityConverter.cs:17`, `BoolToVisibilityConverter.cs:15`, `InverseBoolToVisibilityConverter.cs:15`, `InverseZeroToVisibilityConverter.cs:13`, `ZeroToVisibilityConverter.cs:20`, `PeakToHeightConverter.cs:29`, `PeakToWidthConverter.cs:37`), `NotSupportedException` (`FileIconConverter.cs:20`, `ProcessIconConverter.cs:22`). Исключение конвертера **не перехватывается** движком биндинга. Сейчас безвредно (все `OneWay`), но смена `Mode=TwoWay` в одном месте = краш

Для приложения с `requireAdministrator`, WASAPI и треем — тихое падение посреди сессии без отчёта — худший из режимов отказа.

### H-6. `InputChannelViewModel` — 804 строки, 110 членов, 12 ответственностей

`src/SoundMeeter.Core/ViewModels/InputChannelViewModel.cs` (35 KB)

| # | Ответственность | Строки |
|---|---|---|
| 1 | Имя, переименование, состояние | `:88-121` |
| 2 | Транспорт (volume/gain/mute/mono) | `:123-146`, `:445-473` |
| 3 | Solo-арбитраж с движком | `:475-479` |
| 4 | Параметры денойзера (6 полей) | `:151-170`, `:481-521` |
| 5 | FX-стойка: 4 sub-VM, 13 крутилок | `:172-297` |
| 6 | Матрица маршрутизации | `:340-341`, `:523-558` |
| 7 | Скрытие bus-устройств (уничтожает объекты движка) | `:546-550` |
| 8 | Конечный автомат FUNC-кнопок | `:571-738` |
| 9 | Пайринг виртуального кабеля | `:762-793` |
| 10 | Drag-drop политика приложений | `:38-86` |
| 11 | Списки приложений (принадлежат `MainViewModel`) | `:352-355` |
| 12 | Анимация спада пиков | `:798-803` |

26 вызовов `_markDirty()`, переданного голым `Action`, который вызывается из 26 мест, ни одно из которых не знает, что он делает.

**Что вынести:** `StripRoutingViewModel` (#6+#8), `StripEffectsRackViewModel` (#5), `DenoiserSettingsViewModel` (#4 — в UI уже есть `DenoiserSettingsView`!), `AppRoutingTargetViewModel` (#10+#11).

### H-7. `MainViewModel` — 1 606 строк в 7 partial-файлах, 153 члена, 11 зависимостей

```
MainViewModel.cs              296   ctor/жизненный цикл/язык/тулбар
MainViewModel.Routing.cs      409   устройства, списки приложений, поиск, drag-drop
MainViewModel.Midi.cs         355   диспетчер MIDI + 6 switch-таблиц на строки
MainViewModel.ObsDock.cs      267   жизненный цикл HTTP-сервера, патч OBS user.ini
MainViewModel.Persistence.cs   97   восстановление пресета, dirty, таймер сохранения
MainViewModel.Behaviour.cs     98   трей + реестр автозапуска
MainViewModel.Update.cs        84   проверка релиза GitHub
```

Разбиение на файлы **спрятало** связанность, а не убрало: `MainViewModel.Update.cs` лезет в `_engine.Midi` (`:23`), а `MidiBindingsViewModel` лезет обратно в `_main.Engine.Midi` (`:238,247,257,273,314`). Циклическая зависимость, которая компилируется только потому, что это один тип.

### H-8. Статика `Loc` / `AppLog` — 131 обращение, 15 файлов

Задача в `Tasks.md:23` говорит «~99 мест» — на деле больше на треть.

| Файл | `Loc.` | `AppLog.` |
|---|---|---|
| `LogViewModel.cs` | 6 | 11 |
| `MainViewModel.cs` | 16 | 0 |
| `ObsDockSettingsViewModel.cs` | 15 | 0 |
| `MidiBindingsViewModel.cs` | 11 | 0 |
| `UpdateViewModel.cs` | 10 | 1 |
| `InputChannelViewModel.cs` | 10 | 0 |
| ещё 9 файлов | 53 | 1 |

**Нетестируемы полностью:** `ObsDockSettingsViewModel`, `MidiBindingsViewModel`, `LogViewModel`, `UpdateViewModel` — у каждого единственное наблюдаемое состояние (текст статуса) локализовано и потому неassert-ается.

**Что делать:**
```csharp
public interface ILoc {
    event EventHandler? LanguageChanged;
    string Get(string key, params object?[] args);
    void SetLanguage(string code);
}
```
`Loc` → `LocAdapter : ILoc` синглтоном в `App.xaml.cs`. `LocalizedViewModel` подписывается на **событие инстанса** — попутно уходит статическая утечка подписчиков из L-6. `AppLog.For<T>()` → `ILoggerFactory` из DI, 4 инициализатора полей становятся параметрами конструктора.

### H-9. Вся пайплайн обновления устройств не привязана UI — и каждые 2 с убивает состояние списка

`MainViewModel.Routing.cs:16,20` объявляют `_allAudioDevices` и `HiddenDevices`.

**Ни один XAML в `src/` не биндит ни `_allAudioDevices`, ни `HiddenDevices`, ни `AudioDeviceViewModel`.** При этом каждые 2 с приложение перечисляет все WASAPI-эндпоинты, создаёт новый `AudioDeviceViewModel` на каждый, пересобирает `HiddenDevices`, и `DistributeAppsToDevices` (`:372-384`) **очищает и заново создаёт** `AppViewModel` на каждое работающее приложение.

`RunningApps` **привязан** (`RunningAppsView.xaml:40`) → **выделение и позиция скролла уничтожаются каждые 2 секунды.**

Мёртвый код, который при этом ещё и работает: `AudioDeviceViewModel` целиком (57 строк, 3 команды), `HideDevice`, `DistributeAppsToDevices`, carry-over `existingAssigned` (`:326,336-339`), который тут же перезатирается.

### H-10. Утечки `LocalizedViewModel` через статическое событие

`LocalizedViewModel.cs:42` — `Loc.LanguageChanged += ...` (статика), снимается только в `Dispose()` (`:77`).

| Окно | Освобождается? |
|---|---|
| `MidiBindingsWindow.xaml.cs:24-28` | ✅ |
| `ObsDockSettingsWindow.xaml.cs:18` | ✅ |
| **`LogWindow.xaml.cs:22-24`** | ❌ **нет `Closed` вообще** |
| **`UpdateWindow.xaml.cs:19`** | ❌ + `InstallCompleted` лямбда не снимается |

Окно логов уходит открытым, так что пользователь это заметит.

Плюс **`OutputOptionViewModel` не диспозится нигде** — а он `LocalizedViewModel`. Каждое обновление устройств **навсегда** утекает `2 × (число шин)` инстансов в список подписчиков `Loc`.

И три `+=` анонимными лямбдами (снять их структурно невозможно): `MainViewModel.cs:100,101`, `MainViewModel.ObsDock.cs:265`.

### H-11. Не-атомарное обновление поверх работающего приложения, без бэкапа и отката

`UpdateApplier.cs:363-372` — `robocopy $src $dst /E` пишет **поверх**.

- Нет staging-каталога, нет «переименовать и свопнуть»
- Копирование, упавшее на файле 40 из 200, **уже заменило** файлы 1-39 → новый `SoundMeeter.exe` со старыми сборками
- `Fail` показывает MessageBox и `exit 1` — **после того, как приложение уже вышло**. Пользователь остаётся со сломанной установкой и без способа запустить программу
- Бэкапа нет нигде в модуле
- `plan.NewTotalBytes` считается (`:97`) и **только показывается** — проверки свободного места нет
- `$trash` (`:257-258`, `:392-394`) — мёртвый код: уникальный путь создаётся, передаётся в скрипт, проверяется и удаляется. Задуманный атомарный своп через rename-to-trash **не реализован**

---

## Средний приоритет (MEDIUM)

### Аудио

- **Нет лимитера на шине.** Бюджет усиления до +96 дБ (input +60 × fx +24 × bus +12 × send ×4). `MixingSampleProvider` суммирует 3-4 полосы → хард-клиппинг. `BusDsp.cs:72` `MathF.Pow(10f, db/20f)` без проверки на конечность — NaN проходит в render-клиент
- ~~**Переполнение кольца молчаливое**~~ **Исправлено.** `RingCursor` теперь считает перешагнутое в `SkippedFrames`, а текущую глубину очереди отдаёт в `BufferedFrames` — «пропал звук на стрипе» и «звук отстаёт» больше не приходится угадывать
- **Нет контроля denormal вне денойзера** — `DelayDsp._lowPass/_buffer`, `ReverbDsp.Comb._store`, `Allpass._buffer` затухают геометрически и **секунды** после reverb-хвоста гоняют subnormal-арифметику на захватывающем потоке. Единственный flush-to-zero в проекте — `DenoiserDsp.cs:381`
- **Всё в LOH и переаллоцируется.** ~1.26 MB на input-полосу (`SampleRingBuffer` 384 KB + 2×`DelayLine` по 384 KB + reverb 103 KB), **всё > 85 KB**, и пересоздаётся на каждом `(re)start` источника, то есть на каждом `Start`/`Stop` и при каждом демонтаже устройства
- **Формат сэмплируется один раз и не перепроверяется** — `InputSource.cs:129-130`; при реконфигурации эндпоинта `frames = e.BytesRecorded / (...)` будет читать байты в шум
- ~~**Гонка в `InputSource.Start`**~~ **Исправлено.** `_waveFormat` и `_bytesPerFrame` выставляются **до** `StartRecording()`, обработчик подписан до старта, а при исключении подписка снимается и recorder освобождается — `AudioClient` больше не течёт
- ~~**100 мс / 100 мс латентности, настройки нет.**~~ **Исправлено.** `WasapiAudioEngine.cs:697` `new WasapiOut(device, Shared, true, 100)` давал латентность ≈ 200 мс+, причём её нельзя было ни изменить, ни увидеть: NAudio отдаёт наружу только *запрос*, а играл он по `audioClient.BufferSize`. Заменено на `WasapiPlayerBuilder` с бюджетами в `AudioEngineDefaults` (20/25 мс), MMCSS и запросом IAudioClient3-низкой латентности; фактическая величина пишется в журнал при открытии каждой полосы и шины
- **`OpenAllBusesUnlocked` (`:665-667`) чистит карту шин без диспоза** — сейчас недостижимо, но мина на будущее
- **`Catalog` — неживая view без блокировки** (`:29`): `new ReadOnlyCollection<DeviceInfo>(_catalog)` отдаётся каждой полосе
- **`CompressorDsp` считает `MathF.Log10` + `MathF.Pow` на каждый сэмпл** (`:74`, `:91`) — таблица или envelope по блоку бесплатны
- **Обновление = полный тиринг на UI-потоке.** `RefreshDevices()` (`:52-118`) синхронно делает `Stop` + N × (`GetDevice` + `Init` + `Play`) + M × `StartRecording` на диспетчере — слышимый разрыв и заморозка UI на сотни мс. Плюс O(n²) сканы внутри

### Потоки, локи, аллокации

- **`lock` в обоих аудиоколбэках** — `SampleRingBuffer.cs:35` (Write) и `:77` (Read). Если поток C приостановлен GC во время удержания `_sync`, поток R блокируется → underrun → щелчок
- **Логирование с диском внутри колбэка захвата.** `InputSource.cs:256,299,329,342` → `RotatingFileLoggerProvider.Write` (`RotatingFileLoggerProvider.cs:71-100`) = `DateTime.Now` + `StringBuilder` + `lock` + `WriteLine` с `AutoFlush = true` (`:162-165`) — реальный системный вызов **на потоке аудио**. «Мы зафиксировали перегрузку» само является перегрузкой, и срабатывает именно когда поток отстаёт
- **Аллокации в горячем пути:** `InputSource.cs:194-195,199-200,304-306`, `PolyphaseResampler.cs:195` и `:300` (по `float[Taps]` на фазу). ~~`BusTap.cs:56-57` (`new float[requested]` на рендер-потоке)~~ — **исправлено**: `scratch` аллоцируется один раз, а запросы длиннее блока читаются блоками
- ~~**`RingCursor.Read:95` — `% Capacity` на каждый сэмпл**~~ — **исправлено**: кольцо замкнуто, участок разбит максимум на два отрезка и копируется `Array.Copy`
- **`IAudioPolicyConfig` — неподдерживаемый интерфейс, глотает всё.** `AppRouter.cs:70-82` `catch { return ""; }` → вся переадресация молча деградирует без единой ошибки. Плюс полный `RoGetActivationFactory` **раз в 2 с на процесс** (`AudioService.cs:127`)
- **Обычные (не volatile) поля модели читаются аудио-потоками.** `SoloState` сделан правильно, а `VolumeDb`/`IsMuted`/`IsSolo`/`GainDb`/`PeakLevel` и все параметры эффектов — нет. Формально видимость не определена

### Док OBS

- **`/api/state` никогда не обновляется.** `MainViewModel.ObsDock.cs:183` `if (!_dock.IsRunning || _dock.ClientCount == 0) return;`, а `ClientCount` считает **только WebSocket**. Панель, откатившаяся на polling, **видит вечно замёрзший метр** — ровно тот случай, ради которого fallback и придуман
- **Нет лимита соединений и таймаута простоя.** `ObsDockServer.cs:415-418` — 4 KiB буфер на каждое принятое соединение, и блокировка **навсегда** (`token` отменяется только `Stop()`)
- **Неограниченная пересборка WebSocket-сообщений** — `continuation` (`:315`) без предела, при кадре до 1 MiB (`:539-540`) для канала, где «панель шлёт десятки байт»
- **Фрагментированные кадры шлются с неверным opcode** (`:568`) — только первый кадр сообщения может нести data-opcode, дальше `0x0`. Нарушение RFC 6455, сломается при росте payload
- **`Task.WhenAny` не наблюдает проигравшую задачу** (`:188-192`) — `ObjectDisposedException` на каждое штатное отключение панели
- **`ClientsChanged?.Invoke` в `finally` без защиты** (`:210-221`) — исключение от подписчика **не даёт выполниться `tcp.Close()`** → утечка сокета
- **`CancellationTokenSource.Cancel()` под `_gate`** (`:97-98,105-120`) — отмена синхронно зовёт колбэки под монитором. Сейчас не дедлок, но держится на двух неозвученных инвариантах
- **`ObsDockConfig` — `public static`** (`:94`), поэтому «проверку, что OBS закрыт» нельзя обойти невозможно — её можно вызвать в обход
- **Установщик: `WriteDocks` пишет `user.ini` не атомарно** (`:210-217`) — падение между truncate и write портит конфиг OBS
- **Установщик: вставленная строка теряет CRLF** — `:344` и `:349` вставляют голый `\n` в CRLF-файл, ровно то, чего комментарий на `:334-335` запрещает
- **`ObsDockInstaller.cs:30-31` — если `APPDATA` недоступен, путь становится относительным** и `WriteDocks` создаст мусорное дерево рядом с exe
- **Ноль тестов** на весь док — `grep` по `ObsDockServer`, `ObsDockConfig`, `ObsDockCommand`, `obs-dock` даёт 0 файлов

### ViewModel

- **Шторм уведомлений: один клик FUNC ≈ 160 `PropertyChanged` + ~16 `SetRoute`.** `InputChannelViewModel.cs:594-618`: `RestoreBaseRouting` → `ApplyFunc` → `SetEngagedFunc`, и каждый проходит по всем `AllOutputs`, а `AllOutputs` (`:740`) — это `HardwareOutputs.Concat(VirtualOutputs)`, свежий LINQ-итератор на каждое перечисление
- **Нет диспетчера на событиях движка, в отличие от MIDI и дока.** `MainViewModel.cs:229-257` `OnChannelsChanged` / `OnStateChanged` мутируют привязанные `ObservableCollection` напрямую. Сегодня все вызовы с UI-потока, но **ничего этого не гарантирует**
- **Ошибки диспетчера проглатываются.** `DispatcherService.cs:28` `_dispatcher.InvokeAsync(action).Task` — у не-generic `DispatcherOperation` исключение **не доходит** до `.Task`, а уходит в unhandled диспетчера. В сочетании с H-5 — вызовчик делает вид, что всё прошло, а процесс умирает через мгновение
- **`UpdateViewModel.Cancel()` кидает `ObjectDisposedException`** — `:137` `_cancellation?.Cancel()` против `:129-133` `_cancellation.Dispose()` в `finally`. `?.` защищает от null, но не от disposed
- **Закрытие окна обновления не отменяет обновление.** `UpdateWindow.xaml.cs:28-29`: у кнопки Close нет обработчика, `ApplyAndRestart` срабатывает после `Window.Close()` — приложение **переписывает собственную папку установки и выходит**, хотя пользователь явно закрыл диалог
- **Синхронный диск в property-setter'ах.** `LogViewModel.cs:65-73` — смена уровня лога читает до 512 KB и пишет снапшот на UI-потоке. `MainViewModel.Behaviour.cs:69` пишет **реестр** на каждый переключатель
- **Кэш логгеров `AppLog` наполняется до `Initialize` и не чистится** (`AppLog.cs:102-107`) — любой VM, созданный раньше, навсегда кэширует `NullLogger` в process-lifetime `ConcurrentDictionary`
- **Открытие MIDI-устройства в property-setter** — `MidiBindingsViewModel.cs:246-248` блокирующий WinMM `midiInOpen` на UI-потоке, плюс полный рефреш всех клеток всех полос
- **Состояние дублируется в 2-3 местах без источника истины** — `AppSourceDeviceId` `readonly` (`:46`) при изменяющемся кабель-пайринге; `DockUrl` без `PropertyChanged`; интервал метра 33 ms в **двух** местах (`MainViewModel.cs:55` и `App.xaml.cs:62`)
- **Нет `CanExecute` вообще** — ноль вхождений в `Core`. `FuncButtonViewModel.TurnOn` (`:181-187`) проверяет состояние императивно, но `IsEnabled` не меняется и `NotifyCanExecuteChanged` не вызывается

### UI / XAML

- **129 захардкоженных hex-цветов в 19 XAML-файлах, ноль токенов.** `MixerTheme.xaml` — 38 литералов. `#3AA0E0` встречается 4 раза. Регистр непоследователен: `#1a1a1a` в `StripAppsView.xaml:21` против `#1E1E2E` в `MainWindow.xaml:11`. Тёмная тема из `DarkTheme.xaml` для микшера **непригодна**
- **7 попапов материализуются eagerly на каждый input.** `InputStripView.xaml` объявляет 8 `Popup` (строки 271,298,324,334,344,354,364,374), все с `AllowsTransparency="True"`. **WPF создаёт `Popup.Child` на этапе парсинга XAML** — ленивого контента нет. При 8 полосах это ~56 живых скрытых поддеревьев
- **`SHGFI_LARGEICON = 0x0`** (`ShellIcons.cs:108`) — константа врёт: реальное значение `0x000100`, то есть просятся **мелкие** иконки. Все иконки мыльные на HiDPI
- **`ShellIcons.FromProcess` делает блокирующие kernel-вызовы на UI-потоке, из биндинга.** `ShellIcons.cs:50-52` `Process.MainModule` открывает процесс и читает PEB. Кэш `FileCache` keyed **только по пути** → дорогая часть (`GetProcessById`+`MainModule`) **перевыполняется на каждой переоценке биндинга** для каждой карточки
- **`HorizontalFillPanel` не хит-тестится в зазорах.** `class HorizontalFillPanel : Panel` — у `Panel` нет `Background`, hit-test геометрия пустая между детьми. Полосы имеют `Margin="0,0,10,6"` → **дроп в 10-пиксельном зазоре молча ничего не делает**
- **Нет виртуализации в списках приложений.** `ItemsControl` + `WrapPanel` в `ScrollViewer` (`InstalledAppsView.xaml:58-66`, `RunningAppsView.xaml:39-45`) — `WrapPanel` **невиртуализируем в принципе**. `UpdateSourceTrigger=PropertyChanged` (`:38`) перестраивает список на **каждый символ**
- **Ноль доступности.** 0 `AutomationProperties`, 0 `AutomationPeer`, 0 `FocusVisualStyle`, 0 `IsTabStop`. Иконки-кнопки объявляют глиф (`Content="×"`, `"✕"`, `"＋"`). **Индикатора фокуса нет нигде** — клавиатурный пользователь не видит, где он. `GainKnob` фокусируем, ловит стрелки, но **не `RangeBase` и без `AutomationPeer`** — для скринридера это тупик
- **`GainKnob.Arc` аллоцирует 6 `DependencyObject` на каждую дугу на кадр** (`GainKnob.cs:156-167`) — в `OnRender`, то есть на потоке рендера, при перетаскивании
- **5 скопированных ControlTemplate** (`LedToggle`, `RouteBtn`, `FuncBtn`, `EffectBtn`, `MenuRow`) без `BasedOn`; **6 скопированных 21-строчных блоков слайдеров** в `DenoiserSettingsView.xaml:32-157`; **7 копий оформления попапа**
- **Реестры конвертеров продублированы в трёх местах** под разными именами (`BoolToVis` / `BoolToVisibilityConverter` / `InverseBoolConverter`); `UpdateWindow.xaml` и `MidiBindingsWindow.xaml` **не мерджат `MixerTheme`**
- **Bare `<Style TargetType="TextBlock">` в `MixerTheme.xaml:20-22` затеняет тему приложения** для всего поддерева микшера, включая `LogWindow` и `DevicePickerWindow`
- **Мёртвый код**: `PeakToHeightConverter` и `PeakToWidthConverter` — одинаковая математика, **ноль использований**; `InverseZeroToVisibilityConverter` — ноль использований (`ZeroToVisibilityConverter` с 0.1.2 занял подсказку пустых групп в `MixerView`); `AssemblyInfo.cs:3-10` объявляет `Themes/Generic.xaml`, которого **нет в проекте**
- **`InstalledAppsView.xaml.cs` и `RunningAppsView.xaml.cs` — один файл трижды** (побайтово), `OutputStripView.xaml.cs:64-92` — копия `InputStripView.xaml.cs:106-128`

### Настройки и данные

- **Даунгрейд **уничтожает** `settings.json`.** README обещает обратное (`README.md:270-271`, `README.ru.md:280-281`: «a file from a *newer* build is never overwritten, so a downgrade cannot destroy your settings»). Защита **односторонняя**: `SettingsService.cs:108` `if (HasUnsupportedNewerSchema && !explicitSave)`, а `SaveSync(settings)` (`:90`) идёт с `explicitSave: true`, и его зовёт `SaveNow()` — **путь закрытия окна**. Сценарий: файл `SchemaVersion: 5` → старый билд → `Migrate` возвращает `UnsupportedNewerVersion` → `Restore(null)` стартует с дефолтов → выход → `SaveNow()` пишет **дефолтный снапшот поверх реального файла**. Без бэкапа, без вопроса
- **`lock(null)` на десериализованной коллекции → краш.** `SettingsService.cs:104-105` `var rules = Settings.PersistentRoutes; lock (rules)` → `ArgumentNullException`. Причина: `SettingsMigrator.Migrate` санирует **только при миграции** (`:57` `while (settings.SchemaVersion < CurrentSchemaVersion)`), а валидный файл **текущей** версии проходит без ремонта. Подтверждено на .NET 10: STJ вызывает сеттер и **затирает** инициализатор `= new()`, поэтому явный `"PersistentRoutes": null` даёт `null`. Тот же провал в `MainViewModel.Routing.cs:104` (**без try/catch, на UI-потоке, из команды правого клика**) и `AudioService.cs:170-176`
- **`SettingsService` — 286 строк, ноль тестов.** При этом его собственный тестовый seam **уже есть и не используется** (`SettingsService.cs:35-44` — конструктор с явным каталогом «для проверок без устройств»). Не покрыты: downgrade-путь, corrupt-quarantine, `File.Replace` fallback, `VerifyWrittenFile` (который при битой записи **только логирует**, прямо вопреки своему же doc `:245`)
- **Четыре источника истины для диапазона per-send gain, три разных диапазона, одна лгущая константа:**

  | Место | Диапазон |
  |---|---|
  | `BusTap.cs:31-32` `MinGainDb`/`MaxGainDb` (**это слайдер в UI**) | **−60 … +20** |
  | `SettingsMigrator.cs:127` `Sanitize(…, -60f, 12f)` | **−60 … +12** |
  | `WasapiAudioEngine.cs:284` `Math.Clamp(…, MinGainDb, MaxGainDb)` | **−60 … +20** |
  | `BusTap.cs:76` `else if (gain > 4f) gain = 4f; // +12 дБ` | **хард-клип +12.04 дБ** |

  UI предлагает 8 дБ, которые DSP молча выбрасывает. Рукописный v5-файл с `GainDb: 18` загрузится, покажет «+18.0» и зазвучит как +12
- **`VolumeDb` не клампится нигде на пути записи.** `InputChannelViewModel.cs:445-447` — ни `IsFinite`, ни `Math.Clamp`, тогда как `GainDb` через две строки (`:457`) клампится. Обходят фадер: MIDI (`MainViewModel.Midi.cs:313`) и док (`MainViewModel.ObsDock.cs:216,231`). `+40 дБ` по MIDI сохраняется на диск
- **`lock` на изменяемой коллекции за геттером** в 3 местах (`SettingsService.cs:104`, `MainViewModel.Routing.cs:104`, `AudioService.cs:171`) — идентичность монитора может смениться между двумя «одинаковыми» критическими секциями (`Migrate0To1` делает `??=`, `:114`)
- **`InputChannelModel.cs` — god-модель**: 185 строк, ~45 свойств, три разных природы в одном классе — персистентный DTO, **живое состояние, которое не должно попадать на диск** (`PeakLevel` переписывается каждый пакет и сериализуется при каждом сохранении; `IsAvailable` сохраняет «устройство недоступно» при выходе), и константы (`EffectDefaults` на `:80-95` дублируют литералы на `InputChannelViewModel.cs:231-267`)
- **Пререлиз сравнивается ординально** (`AppVersion.cs:92-93`) → `1.0.0-rc.2 > 1.0.0-rc.10`, пользователь на `rc.2` **не получит** `rc.10`. `AppVersionTests.cs:88` проверяет только `beta.2 > beta.1`, что проходит случайно
- **SourceLink-метаданные превращают сборку в бесконечный цикл обновлений.** SDK выдаёт `1.0.0-rc.1+<sha>`, а `TryParse` (`:71`) режет `+` только **до** пререлиза → `"rc.1+abcdef"` никогда не равно тегу `"rc.1"` → баннер «есть обновление» **при каждом запуске**
- **`AppVersion.Current` молча деградирует в `0.0.0`** (`:35-43`) без строки в логе
- **`SafeFileName` не нейтрализует `.` / `..`** (`GithubUpdateService.cs:292-299`), а следом идёт `Directory.Delete(target, recursive: true)` (`:238`). Сегодня недостижимо (`TryParse` требует цифру), но **страховочки нет**
- **Нет предела распаковки (zip-bomb), и всё это на UI-потоке.** `ZipFile.ExtractToDirectory` без ограничения + `EnumerateFiles(AllDirectories)` + рекурсивный скан папки установки — синхронно внутри async-команды
- **Нет очистки `settings.json.tmp`** — падение между `:221` и `:226-238` оставляет его навсегда
- **Сохранение staging никогда не чистится** — `UpdateRoot/<tag>` и `<asset>.zip` копятся
- **`UpdateViewModel.InstallAsync` живёт 10-минутный таймаут на UI** (`HttpTimeout = 10 min`, `GithubUpdateService.cs:36`) без возможности отмены при закрытии окна
- **Проверки обновлений гоняются без защиты от наложения** — `MainViewModel.Update.cs:30-51`, стартовая проверка fire-and-forget + проверка из тулбара, результаты last-writer-wins без счётчика поколения
- **`StreamInfo`-уровневые вещи в мёртвом `AudioPolicy`** — `PolicyConfig.cs`, `IPolicyConfig.cs`, `CPolicyConfigClient.cs`, `AudioPolicyConfigFactory.cs` (~90 строк интеропа) **не вызываются никем**
- **`ApplyPresetUnlocked` (`WasapiAudioEngine.cs:810-841`) — мёртвый код**, `ApplyPreset` его не зовёт, а doc говорит «накладывает», а код **заменяет** списки. Документация `InputChannelModel.BusRouting` говорит, что ключ — `DeviceId` устройства вывода, а это **Id шины** (`:731`, `:866`)

---

## Низкий приоритет (LOW)

**Аудио**
- `TrackPacketSize` считает миллисекунды против 48 kHz независимо от реальной частоты (`InputSource.cs:332`)
- `_maxPacketBytes` сбрасывается только в `Stop()` (`:159`), а `_warnedBacklog` перевзводится (`:347-350`) → спам в лог
- `CablePairing.IsVirtualCableName` матчит подстроку `"virtual"` (`:40`) → любое устройство с «virtual» в имени предлагается как цель
- `AppRouter.ParseEndpoint`/`FormatEndpoint` делают строковую хирургию над `\\?\SWD#MMDEVAPI#…` (`:41-60`) без валидации
- `RnNoiseInterop.ProcessFrame` (`:68-73`) пинит массивы **без проверки длины** — короткий массив = AV и падение процесса
- Нет финализатора/`Dispose`-паттерна у `DenoiserDsp`; `SuppressGCTransition` убрал бы 200 P/Invoke/с на полосу
- `AudioService` опрашивает `process.MainModule?.FileName` каждые 2 с на каждую аудиосессию

**ViewModel**
- `FuncTargetViewModel.cs:21-26` и `OutputOptionViewModel.cs:30-35` — **посимвольно одинаковые** `_channelName` + `Title` + `ToolTip`
- `0.002f` и `0.8f` затухания пика продублированы в `InputChannelViewModel.cs:798-803` и `OutputBusViewModel.cs:127-132`
- `MidiBindingsViewModel.cs:57` `ModeOptions` аллоцирует массив **на каждое чтение**
- `OnPropertyChanged("")` при смене языка обходит ~58 × N объектов без батчинга
- `MainViewModel.Behaviour.cs:69-81` — намеренная саморекурсия в своём же обработчике без флага-реентрантности

**UI**
- `M6` выше: `DispatcherService.InvokeAsync` глотает исключения
- `MainWindow.xaml.cs:37-38` — синхронный `Dispatcher.Invoke` для VM→UI колбэков без проверки потока (в отличие от всех остальных мест, где используется `HasThreadAccess`/`Post`)
- `MainWindow.xaml` — единственное окно без `MinWidth`/`MinHeight`, при фиксированной ширине полосы 138 px
- `ShutdownMode` нигде не задан → дефолт `OnLastWindowClose`
- `App.xaml.cs:125-146` — три пустых `catch { }`, первый глотает падение диспозит DI **без строки в лог**, потому что `AppLog.Shutdown()` идёт после
- `MainWindow.xaml.cs:63-66` — `Topmost = true; Topmost = false;` ворует фокус безусловно
- `GainKnob` — `CaptureMouse()` не проверяется, нет `LostMouseCapture`, колесо без `Ctrl` для точной подстройки
- `AppDragSource._pressStart` не сбрасывается на `Unloaded`; кнопка `✕` — **ребёнок** drag-контейнера, клик по ней взводит drag
- `HorizontalFillPanel.OnDragOver` делает `FindSourceIndex` на **каждое** событие перетаскивания (`:129`)
- 15 вызовов `Loc.Get` в коде-бэкенде, ни один не реактивный
- `DevicePickerWindow` — мини-MVP-нарушение: `DataContext` не задан (при этом `Title="{Binding Title}"` в XAML мёртв), фильтрация целиком в коде-бэкенде
- `LogWindow` грузит весь лог-файл в один `TextBox` (`LogWindow.xaml:82-91`)
- Unicode-глифы как контент (`"●"`, `"◄"`, `"＋"`, `"→"`) зависят от шрифта и рисуют «тофу»
- `Loc.SetLanguage` ставит `CurrentCulture`, но **не** `DefaultThreadCurrentCulture` → всё, что форматируется на пуле (логи, сообщения VM), остаётся в системной культуре
- Комментарий в `MainWindow.xaml.cs:21-23` о том, что `Language` влияет на `StringFormat`, **неверен** — WPF использует `CultureInfo.CurrentCulture`

**Репозиторий**
- `.obsidian/*.json` (5 файлов) — личное состояние vault в git; стоит в `.gitignore`
- Смешанная политика BOM: `.csproj` **с** BOM, большинство `.cs`/`.xaml` **без**; нет `.gitattributes`
- `SoundMeeter.StartUp.csproj` объявляет `<InternalsVisibleTo Include="SoundMeeter.StartUp.Tests"/>`, а такого проекта нет
- `CoreValueTests.cs:12` — файл `AppBehaviourSettingsTests`, имя файла ≠ имя класса; лежит в `Audio.Tests`, а тестирует `AppSettings`
- `ParamResetTests.cs:112-116` — комментарий про порог компрессора стоит над ассертами про `DelayTimeMs`
- `StripEffectTests.cs:96-97` — `Assert.Equal(0f, Minimum + 60f, 2)` вместо читаемого `Assert.Equal(-60f, Minimum)`; проверяется только 1 диапазон из 13
- `GlobalUsings` генерируются с BOM — без `.gitattributes` это diff-шум
- **Все 13 диапазонов эффектов сейчас совпадают** между `InputChannelViewModel.cs:231-267`, `SettingsMigrator.cs:203-218`, `CompressorDsp.cs:47-52`, `FxGainDsp.cs:31`, `DelayDsp.cs:21,25,48-51`, `ReverbDsp.cs:89-91` — проверено вручную. Но **ничем не закреплено**. `DelayDsp.MaxTimeMs=2000`/`MaxFeedback=90` — приватные константы-дубликаты

---

## Архитектура и рефакторинг

### God-интерфейс `IAudioEngine` (27 членов + `Dispose`)

Одна реализация, один фейк на 60 строк с 19 no-op'ами. Естественные швы:

| Шов | Члены | Зачем |
|---|---|---|
| `IAudioEngineTransport` | `IsRunning`, `Start`, `Stop`, `StateChanged` | Только жизненный цикл |
| `IDeviceCatalog` | `Catalog`, `RefreshDevices`, `ChannelsChanged` | Тестируемость `RefreshDevices` (сейчас 0) |
| `IStripEditor` | `Inputs`, `Buses`, `Add*`/`Remove*`/`Move*`, `Set*Source` | Владеет `_gate` |
| `IMixMatrix` | `SetRoute`, `SetRouteGain`, `SetInputSolo`, `SetOutputSolo` | Единственные, кому **обязан** работать под `_gate` во время микса |
| `IMixerPresetService` | `CreateSnapshot`, `ApplyPreset` | Чистые данные; здесь же решить replace-vs-overlay |
| **Убрать из движка** | `Midi { get; set; }` | MIDI-настройки не имеют отношения к WASAPI |

Оставить `IAudioEngine` как фасад (`sealed interface IAudioEngine : …`), чтобы ~20 мест вызова не churn'нули разом.

### Рефакторинг ViewModel

Тест соприкасается с приватной статикой через рефлексию — `Core.Tests/MidiBindingTests.cs:113-134`:

```csharp
private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
private static bool GetButtonState(InputChannelViewModel vm, string parameter) =>
    Method(nameof(GetButtonState), …).Invoke(null, [vm, parameter]) is true;
```

157 строк `switch` по строковым именам параметров (`MainViewModel.Midi.cs:198-354`) стали приватными-статическими **специально ради тестируемости**. Правильный фикс — `internal` + `InternalsVisibleTo` (или публичная точка входа `IMidiTargetResolver`), а не рефлексия. Переименование любого из `IsButtonParam`/`GetButtonState`/`SetButtonState`/`ToggleButton` ломает 3 теста при компилируемой сборке. То же в `FuncButtonTests.cs:255-264` и `VisualTree.cs:84-89` (лезет в приватный `ButtonBase.OnClick` **BCL** — смена внутренности в .NET уронит сьют).

Ещё две находки, которые стоит проверить:
- `IInstalledAppsService`, `IInstalledAppsService.cs` и `ISettingsService` (2 члена) — фасады, которые большинство потребителей **обходят**: `MainViewModel` и `LogViewModel` берут **конкретный** `SettingsService`, потому что интерфейс не отдаёт `HasUnsupportedNewerSchema` / `LoadReport`. Утечка абстракции, а не абстракция
- `Core` объявляет `RuntimeIdentifier=win-x64` **ради нативных ассетов** `rnnoise.dll` — правильное решение задокументировано, но это навсегда привязывает ядро к win-x64

---

## Тесты и CI

### Сейчас

| Проект | Тестов | Файлы |
|---|---|---|
| `SoundMeeter.App.Tests` | 52 | `FuncButtonTests` 20, `ModuleBoundaryTests` 9, `ParamResetTests` 8, `StripEffectTests` 8, `AppIconTests` 7 |
| `SoundMeeter.Audio.Tests` | 54 | `DenoiserDspTests` 23, `StripDspTests` 11, `SampleRingBufferTests` 7, `SettingsMigratorTests` 5, `AudioModuleBoundaryTests` 4, `CoreValueTests` 4 |
| `SoundMeeter.Core.Tests` | 11 | `MidiBindingTests` 6, `AudioEnginePresetTests` 3, `CoreWpfFreeTests` 2 |
| `SoundMeeter.Update.Tests` | 10 | `AppVersionTests` |
| `SoundMeeter.ChangeLanguage.Tests` | 6 | `LocTests` |

**120 тестов, ~3 с, без аудиоустройств и без админа.** Это хороший результат для 14 проектов.

### Что не покрыто

| Тип | Почему |
|---|---|
| `DenoiserDsp` | закрыто: `DenoiserDspTests` 23 теста (C-1, C-1a, C-1b, C-1c) |
| `PolyphaseResampler` | **нулевые тесты** — самый сложный компонент (рациональное отношение, 65-257 taps) |
| `BusTap`, `InputSource`, `BusDsp` | **нулевые тесты** |
| `SettingsService` | 286 строк, ноль тестов, при готовом seam |
| `ObsDockServer`, `ObsDockConfig`, `ObsDockInstaller` | **ноль файлов** |
| `GithubUpdateService` | выбор ассета, `SafeFileName`, `FindPayloadRoot` — 0 |
| `AuthenticodeVerifier`, `UpdateApplier` | 0 |
| `AudioDeviceViewModel`, `InstalledAppViewModel`, `AppViewModel`, `ConfiguredAppViewModel` | тривиальные, но фильтр `FilteredInstalledApps` не покрыт |
| `EffectKnobViewModel`, `StripEffectViewModel` | клампинг диапазонов не покрыт (и **13 из 14 диапазонов** не сверяются с DSP) |
| `OutputBusViewModel`, `OutputOptionViewModel`, `FuncTargetViewModel` | **чистые от статики — самые дешёвые победы** |

### Проблемы харнесса

- **`UiHost.cs:73` — результат `SpinWait` отбрасывается.** `SpinWait.SpinUntil(() => _ready, TimeSpan.FromSeconds(30))` — если STA-поток не дошёл до `_ready = true` и `_startupFailure` при этом null, исполнение проваливается на `:79` `Application.Current.Dispatcher` → `NullReferenceException` вместо внятной ошибки. Если поток умер **после** установки `_ready`, `:95` блокируется на все 2 минуты таймаута. Худший случай — **2.5 минуты на UI-тест с вводящим в заблуждение исключением**. Плюс `SpinUntil` жжёт ядро 30 с
- **В `UiHost` нет `DispatcherUnhandledException`** — исключение из внутренностей WPF убивает процесс, xunit сообщает о crash, а не о failure
- **Тесты зависят от состояния машины**: `AppIconTests.cs:31-32` читает `%SystemRoot%\System32\notepad.exe` (на Win11 24H2+ это Store-reparse-заглушка); `:96` извлекает иконку из живого процесса test-host; `AudioEnginePresetTests.cs:47,63,84` конструирует **настоящий** `WasapiAudioEngine`
- **`AppIconTests.cs:53-63` делает `Assert.Same` по статическому кэшу** — order-independent, но stateful
- **Нет `[Trait]`, `[Collection]`, `xunit.runner.json`** — нет лимита параллелизма. UI-тесты сериализуются только *побочно* благодаря одному диспетчеру `UiHost`
- **Пинны инструментов отстали на ~2 мажора**: `Microsoft.NET.Test.Sdk` 17.8.0 → 18.10.1, `xunit` 2.5.3 → 2.9.3, `xunit.runner.visualstudio` 2.5.3 → 4.0.0. Внутрисемейный разнобой в App: DI 10.0.12 vs Logging 10.0.11 vs Abstractions 10.0.11
- **Ни покрытия, ни мутационного тестирования, ни бенчмарков DSP**

### `LocTests` — тест неполноты локализации

`LocTests.cs:40-48` `EveryResourceFileKeyResolves` читает только `Loc.ReadAllValues()` для **текущей** культуры. Ключ, который есть в `Strings.resx`, но отсутствует в `Strings.ru.resx`, **пройдёт**. Теста на паритет ключей между resx-файлами нет.

### Про зависимости

Предположение «NAudio 3.1.0 устарел» — **неверно**: 3.1.0 это последняя стабильная (индекс 63 из 66, дальше только `3.1.1-preview`). `dotnet list --vulnerable --include-transitive` чист на всех 14 проектах, `NuGetAudit` включён по умолчанию и даёт 0 предупреждений.

Настоящая проблема **уже, чем версия**: `<NoWarn>$(NoWarn);CS0618</NoWarn>` в 4 проектах навсегда глушит `[Obsolete]` на `WasapiOut`/`WasapiCapture`. Проект построен на документированно устаревшем API с permanently-suppressed предупреждением и без трекинг-задачи.

---

## Документация

### README (обе версии) содержит неверные утверждения

| Утверждение | Реальность |
|---|---|
| `README.md:122` / `.ru:130` — «120 xUnit tests across **six** projects» | проектов **5** (та же ошибка в `Tests.md:3`) |
| `README.md:248` / `.ru:257` — «Per-route `GainDb` is *not* applied in the audio path - strip volume is the only gain source» | **Ложно.** `BusTap.cs:66,74`: `float send = DbToLinear(_routing.GainDb); float gain = volume * send;` |
| `README.md:270-271` / `.ru:280-281` — «a downgrade cannot destroy your settings» | **Ложно**, см. C-1 выше |
| `README.md:170-172` — границы модулей «enforced by tests rather than convention» | **Обходимы**, см. H-3 |
| `README.md:50` — самообновление через `robocopy /MIR` | `/MIR` **удалён** (комментарий `UpdateApplier.cs:24-27`, скрипт использует `/E` на `:365`) |
| `README.md:334` — лицензия «To be determined» | файла лицензии нет вообще |
| — | **Процесса релиза не описано.** Как собрать — есть; как выпустить — нет |

### `modules-summ.md` (73 KB, 704 строки) — это транскрипт сессии ИИ-агента

Содержит секцию `LAST_ACTION` с подразделом «### Resolved (следующий turn)», фразы «Теперь предлагаю:» и заканчивается «Скажешь, продолжать?». Пересказывает XML-док-комментарии (которые и так хорошие) и **устарел минимум в 7 местах**:

| Заявлено | Реальность |
|---|---|
| `<Version>1.0.0</Version>` | `SoundMeeter.App.csproj:30` = `0.0.1` |
| `SoundMeeter.csproj` | `SoundMeeter.App.csproj` |
| `Models/AppVersion.cs`, `Services/UpdateService.cs` | `SoundMeeter.Update/AppVersion.cs`, `GithubUpdateService.cs` |
| «MainViewModel на 10 partial-файлов» | 7 файлов |
| `dotnet build StreamerTools.slnx` падает на `Main.vcxproj` | решение `SoundMeeter.slnx`; `.vcxproj` в репозитории нет |
| «все пять C#-проектов» | 14 проектов |

Упоминает `Temp\opencode\obs-dock-harness`, который `Tests.md:65` говорит удалённый.

**Вердикт: чистый минус.** Удалить, либо свести к рукописной карте архитектуры, которая **ссылается** на исходники, а не дублирует их.

### `Tasks.md` и `Tests.md` устарели

- `Tests.md:11` утверждает, что `Update.Tests` покрывает «выбор ассета релиза» — **ложно**, `AppVersionTests.cs` только парсинг/сравнение semver
- `Tests.md:12` / README «все языковые файлы полны» — см. выше, паритета ключей нет
- `Tests.md:63` — устаревшее имя решения и «пять проектов»
- `Tasks.md` — пункты без issue ID, дат и исполнителей → **неисполнимо**. Часть непомеченных пунктов уже сделана: «Улучшить Denoiser» (RNNoise + 4-полосный formant EQ + dry/wet есть), «Min/Max лимит на VU-meeter» (границы есть в `SegmentedMeter.cs`/`GainKnob.cs`), «Ссылка на гитхаб» (remote есть)

### Сабмодуль `StreamerTools.Style`

Закреплён (`243721cd`), инициализирован, чистый. 136 файлов, 6.4 MB рабочего дерева, из них **~6.24 MB — сгенерированные данные иконок**:

| Файл | Размер |
|---|---|
| `Extensions/PackIconDataFactory.cs` | 2746 KB |
| `Controls/Icons.cs` | 2709 KB |
| `Extensions/PackIconKind.cs` | 394 KB |
| `Controls/EIcons.cs` | 394 KB |

`PackIconDataFactory.cs:4-6` говорит `/// This code is auto generated. Do not amend.` — но **генератора в сабмодуле нет**, так что файлы невоспроизводимы из исходников.

Родительский репозиторий это не раздувает (только gitlink, `.git` = 2.7 MB), текущая сборка быстрая (8.4 с на всё решение). Но 6.2 MB сгенерированного C# парсится Roslyn на каждой чистой сборке и в каждой сессии IDE и блокирует любой анализ. Варианты: отдать иконки `.ttf`/`.svg` + пакет `PackIcon`, либо сделать генератором проект в сабмодуле.

---

## Что сделано хорошо

Чтобы не переаудировать при следующем проходе:

- ✅ `bin/` + `obj/` — **0 из 207** отслеживаемых файлов. `.gitignore` работает. Все 156 записей `git status` — это **staging- set переименований** (`SoundMeeter` → `SoundMeeter.App`), а не build-вывод. Untracked: **0**
- ✅ `src/SoundMeeter.slnx` — 14 записей, 14 каталогов проектов, точное совпадение. Ничего не потеряно и ничего лишнего
- ✅ **0** `TODO` / `FIXME` / `HACK` / `XXX` / `WIP` во всех 132 production и 21 тест-файлах
- ✅ **0** закомментированных блоков кода
- ✅ **0** абсолютных личных путей в исходниках и документации
- ✅ Нет секретов в `settings.json` — только имена MIDI-устройств, ID устройств, пути и loopback-порт
- ✅ **0** `xunit [Skip]` — ничего не тихо отключено
- ✅ Комментарии в коде — **сильные**. Границы модулей, `UseWPF`, `RootNamespace`, история переезда, «почему так, а не иначе» — читается лучше, чем в большинстве проектов. Это редкость, и её не надо терять
- ✅ `StripDspTests.cs` — настоящие тесты на сигнале: побитовая прозрачность при bypass, инъекция NaN/Inf (`:152-172`), сброс хвоста (`:174-190`). Не «метод не бросил»
- ✅ `SampleRingBufferTests` — граничная логика кольца доказана
- ✅ Разделение на Core/App/Audio — свежее и правильное. `UseWPF` вынесен из ядра **осознанно**, с объяснением куда уехало каждое использование, и граница закреплена тестом
- ✅ Кастомные контролы на DependencyProperty, а не на CLR-свойствах
- ✅ Пайплайн пиков сделан правильно: `PeakLevel` → DP `AffectsRender` → `OnRender`. Инвалидация только рендерная, не layout
- ✅ `Loc.SetLanguage` + `LocalizedViewModel` через `OnPropertyChanged("")` — рабочая и правильно документированная схема
- ✅ `ObsDockInstaller` сохраняет `user.ini` в `.bak` перед первой правкой
- ✅ XAML-локализация чистая: единственные литералы — глифы и `Title="SoundMeeter"`
- ✅ `UnregisterEndpointNotificationCallback`-подобная дисциплина в `FuncButtonSettingsView.xaml.cs:32` — эталон правильной пары подписка/отписка, на который надо ссылаться при починке H-10

---

## План работ

> [!tip] Порядок выбран по «стоимость деления ущерба на стоимость фикса», а не по приятности

### Этап 0 — за день, без риска

Остановить кровотечение. Ни один пункт не меняет архитектуру, все проверяются тестами.

- [ ] **`LICENSE`** — выбрать и закоммитить (H-1)
- [ ] **`DispatcherUnhandledException`** в `OnStartup` до всего остального + `try/catch` на хвост `OnStartup` после первого `await` (H-5)
- [ ] **`async void` → `Task`** в `MainViewModel.cs:100-103`, `Routing.cs:112` (C-2)
- [ ] **`try/catch` в `SavePool`** (C-3) + `_saveTimer.Dispose()` в `DisposeCore` (C-4)
- [ ] **`SaveSync` на пути закрытия окна уважает downgrade-guard** — добавить бэкап перед первым explicitSave и/или не давать explicitSave затереть файл новой схемы (C-1 настроек)
- [ ] **Санитайзер, работающий на файлах текущей версии** — `SettingsMigrator` должен чинить `null`-коллекции всегда, не только при миграции (C-1 настроек / `lock(null)`)
- [ ] **Диспоз `device`/`output` в `OpenBusUnlocked` при неудаче** (C-6)
- [ ] **`app.manifest`: DPI PerMonitorV2 + `longPathAware` + `supportedOS`** (H-4)
- [ ] **`LICENSE` + `ShutdownMode=OnExplicitShutdown`** + проверка `MinWidth` у `MainWindow`
- [ ] **Исправить 3 неверных утверждения в README** (число проектов, `GainDb`, downgrade) — это 5 минут и снимает ложные обещания

### Этап 1 — неделя: безопасность

- [ ] **Токен в доке.** 128 бит при `Start()`, `?t=` в URL, обязателен на `/ws`, allowlist `Origin`, проверка `Host` (C-9). **Одна правка закрывает и rebinding, и локального пользователя**
- [ ] **Подпись сборок + SHA-256 ассета.** Это несколько дней работы (сертификат, пайплайн подписи, пинованный thumbprint), но без неё автообновление — это просто загрузка и запуск кода с правами администратора (C-8)
- [ ] **Абсолютный путь к `powershell.exe`**, `-ExecutionPolicy RemoteSigned` вместо `Bypass`, ACL на временный `.ps1` (C-8 / H-2-H-3 раздела обновления)
- [ ] **Атомарный своп с бэкапом** и staging-каталог (H-11)
- [ ] **Закрытие окна обновления отменяет установку** — обработчик `Closing`
- [ ] **Проверка схемы URL ассета** — только `https://github.com/…`, `HtmlUrl` только `https://` + `github.com`

### Этап 2 — неделя: звук

- [ ] **Денойзер теряет звук.** Размер очередей от фактического буфера, обработка шагами по 480, **никогда** не дозаполнять нулями + тесты на 10/25/50/100 мс (C-1)
- [ ] **Развязать аудиопотоки.** Убрать `lock` из `SampleRingBuffer` (SPSC-кольцо на `Interlocked`), вынести логирование с аудиопотока в фонового писателя (C-2 раздела аудио, «логирование с диском в колбэке»)
- [ ] **Восстановление устройств.** Подписки на `PlaybackStopped`/`RecordingStopped` + `RegisterEndpointNotificationCallback` + один путь `ReopenBus/ReopenSource` (C-5)
- [ ] **Адаптация формата.** `IsFormatSupported` + подстройка числа каналов (C-7)
- [x] **Латентность — настройка.** `WasapiRecorderBuilder.WithBufferLength(20)` на входе, `WasapiPlayerBuilder.WithLatency(25)` + `WithLowLatency()` на выходе вместо 200 мс сквозных (H-«100 мс / 100 мс»); бюджеты вынесены в `AudioEngineDefaults`, фактическая латентность пишется в журнал
- [ ] **Лимитер на шине** + счётчик клиппинга, FTZ/DAZ в хвостах delay/reverb
- [ ] **Кольцо и LOH.** Переиспользовать per-strip буферы между рестартами источника, размер кольца от согласованного буфера

### Этап 3 — неделя: инфраструктура

- [ ] **CI**: `.github/workflows/ci.yml` — `dotnet build -c Release` + `dotnet test` на push/PR
- [ ] **`global.json`** (пин SDK) + **`Directory.Packages.props`** (центральные версии) + **`packages.lock.json`**
- [ ] **`.editorconfig`** + **`.gitattributes`** (нормализация EOL и BOM)
- [ ] **`.gitignore` для `.obsidian/*.json`**
- [ ] **Граничные тесты по `.csproj`**, а не по метаданным сборки; добавить `StreamerTools.Style` в список модулей (H-3)
- [ ] **Процесс релиза**: `publish.ps1` + `.pubxml` в репозитории + раздел в CONTRIBUTING
- [ ] **Обновить xUnit-туллинг**
- [ ] **Удалить `modules-summ.md`** или свести к рукописной карте архитектуры со ссылками на исходники

### Этап 4 — две-три недели: тестируемость и рефакторинг

Порядок выбран так, чтобы каждый шаг был самостоятельно поставляемым:

- [ ] **`SettingsService` — тесты на существующем seam** (`SettingsService.cs:35-44`). Ноль тестов на 286 строк — самая дешёвая большая победа
- [ ] **`ILoc` + DI-логгер** вместо 131 статического обращения (H-8). Попутно уходит утечка подписчиков из H-10
- [ ] **`internal` + `InternalsVisibleTo` вместо рефлексии** в `MidiBindingTests`, `FuncButtonTests`, `VisualTree` (раздел «Архитектура»)
- [ ] **Разбить `IAudioEngine`** на 5 швов за фасадом
- [ ] **`StripRoutingViewModel`** из `InputChannelViewModel` (H-6, снимает ~1/3 ответственностей)
- [ ] **`IObsDockInstaller`** вместо `new` в `MainViewModel.ObsDock.cs:13`
- [ ] **Тесты на док**: `Origin`, токен, `Host`, лимит кадра, INI round-trip на CRLF, пин экранирования в `BuildScriptBlock`
- [ ] **Тест паритета ключей локализации** между `Strings.resx` и `Strings.ru.resx`
- [ ] **Тест на 13 диапазонов эффектов** против констант DSP

### Этап 5 — по времени: UI

- [ ] **Токены цвета** в `MixerTheme.xaml` — 129 литералов в 19 файлах (M-1)
- [ ] **Ленивый контент попапов** — 7 × 2 попапа на полосу, 56 живых поддеревьев (H-5 UI)
- [ ] **Виртуализация списков приложений** — `WrapPanel` невиртуализируем (H-9 UI)
- [ ] **`SHGFI_LARGEICON = 0x100`** + кэш значков по PID (H-6/H-7 UI)
- [ ] **`GainKnob` — кэш геометрии + `Freeze()` кистей** (C-3 UI)
- [ ] **Убрать дубли**: 5 шаблонов, 6 блоков слайдеров, 7 оформлений попапа, 3 реестра конвертеров
- [ ] **Доступность**: `AutomationProperties.Name`, индикаторы фокуса, `GainKnob` как `RangeBase` + `AutomationPeer`, порядок табуляции, клавиатурный reorder
- [ ] **`HorizontalFillPanel` — `Background`/`HitTestCore`**, чтобы дроп в зазоре работал
- [ ] **Мёртвый код**: `PeakTo*Converter` (2), `InverseZeroToVisibilityConverter`, `Themes/Generic.xaml` (отсутствует), `ApplyPresetUnlocked`, `AudioPolicy/*` (~90 строк)

### Этап 6 — по времени: согласованность

- [ ] **Единый источник истины для диапазона per-send gain** — сейчас 4 места, 3 диапазона, одна лгущая константа (M-«Четыре источника истины»)
- [ ] **Клампить `VolumeDb` на пути записи** — MIDI и док обходят фадер
- [ ] **Разобрать god-модель `InputChannelModel`** — персистентный DTO / живое состояние / константы
- [ ] **Собрать UI-привязанный список устройств** — сейчас вся пайплайн мертва и каждые 2 с убивает выделение в `RunningApps`
- [ ] **Batch-нотификация** — один клик FUNC = 160 `PropertyChanged`
- [ ] **Диспетчер на `ChannelsChanged`/`StateChanged`** — сейчас единственное место без маршализации
- [ ] **`Revision` на маршрутизацию** — фиксировать версию снимка в момент drag-start (снимает смешанные подсказки в статусе)
- [ ] **Убрать дубли состояния** — `AppSourceDeviceId` readonly, `DockUrl` без `PropertyChanged`, двойной интервал метра
- [ ] **Правильный semver** для пререлиза + обрезка `+build` до пререлиза (иначе бесконечный баннер) + громкое логирование `0.0.0`
- [x] **Поднять NAudio на замену `WasapiOut`/`WasapiCapture`** — там MMCSS, которого нет у Obsolete-классов, и он снимет часть C-1/C-2 раздела аудио. Сделано: `InputSource` работает через `WasapiRecorderBuilder`, шины — через `WasapiPlayerBuilder`; заодно ушли per-packet `new byte[]` в LOH (ноль-копи буфер) и опрос потока захвата по таймеру

---

## Связанные заметки

- [StreamerTools/Tasks.md](../StreamerTools/Tasks.md) — бэклог фич (часть пунктов уже неактуальна)
- [StreamerTools/Tests.md](Tests.md) — описание текущего харнесса (часть утверждений неверна)
- [StreamerTools/modules-summ.md](../StreamerTools/modules-summ.md) — транскрипт сессии ИИ, предлагается удалить

> [!note] О vault
> `.obsidian/` лежит в `Obsidian/StreamerTools/`, эта заметка — в `Obsidian/SoundMeeter/`.
> Если корень vault — `Obsidian/StreamerTools/`, то папку `SoundMeeter/` надо либо перенести внутрь, либо пересоздать vault на `Obsidian/`. Ссылки выше относительные и работают в любом случае.

## Быстрый индекс по симптому

| Симптом | Вероятная причина |
|---|---|
| Щелчки/провалы звука при нагрузке | `lock` в аудиоколбэках (аудио-M2), логирование с диском в колбэке (аудио-M3) |
| Цифровая тишина на микрофоне с включённым денойзером | **C-1** |
| Приложение падает через несколько секунд после старта | **C-2**, **C-3** |
| Полоса молча умерла после отключения устройства | **C-5** |
| Полоса мертва сразу на многоканальном/моно-устройстве | **C-7** |
| Глухой звук на стриме, которого не было | атака на док OBS (**C-9**) |
| Настройки сбросились при откате версии | **C-1** (раздел настроек) |
| Полоса появилась, но настройки не грузятся, приложение падает | `lock(null)` на `PersistentRoutes` |
| Ползунок per-send gain показывает +18, а звучит как +12 | **M-«Четыре источника истины»** |
| Значки приложений мыльные на HiDPI | `SHGFI_LARGEICON = 0x0` |
| Дроп полосы в зазоре не работает | `HorizontalFillPanel` без hit-test |
| UI подвисает на 2 с каждые 2 с | `RefreshDevices()` на UI-потоке (аудио-M6) + мёртвый `RunningApps` (**H-9**) |
| Баннер «есть обновление» появляется на каждом запуске | SourceLink `+sha` в пререлизе |