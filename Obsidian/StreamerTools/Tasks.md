- [x] Смена языка.
- [ ] Смена стилей.
- [x] Перетаскивание Inputs/Buses.
- [x] Переместить кнопки "Refresh Devices", "Midi", "OBS", "Updates", "Logs" под одну кнопку настроек.
- [x] Добавить Gain для микрофонов
- [ ] Автозапуск
- [ ] Сворачивать в трэй
- [ ] Подключение и управление с телефона
- [ ] Плагин для MiraBox
- [ ] Проверить работу с Midi Mixer Korg
- [x] Компрессия
- [ ] Min лимит и Max лимит на VU-meeter
- [ ] Ссылка на гитхаб
- [x] Reverb
- [x] Бинд на "Read" (на прослушивание самому) (на вывод на стрим или наоборот только в VoiceChat)
- [ ] Улучшить Denoiser
- [ ] Эквалайзер
- [ ] Инструкция к применению
- [ ] Выделение в отдельные проекты Update, Logger, ChangeLanguage, StartUp и TrayIcon

2. Одна честная экстракция: SoundMeeter.Audio (Audio + Models + SettingsMigrator) отдельной сборкой без ссылки на WPF. Тесты ссылаются только на неё и не поднимают WPF. Это ~90% выгоды Clean Architecture при 1/10 риска: добавляется csproj, namespace'ы не меняются.

3. Разделить IAudioEngine (25+ членов, бог-интерфейс) на IRoutingTable / IDeviceCatalog / IStripTopology / IPresetStore. Это настоящая победа SOLID, и она локальная.

4. Убрать статики Loc и AppLog — 99 мест в ViewModels. Вот это реально покупает тестируемость VM.