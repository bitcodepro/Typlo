using Avalonia.Markup.Xaml;

namespace Typlo.App.Services;

/// <summary>
/// UI strings in English (default), Russian and Ukrainian. Windows read them when created,
/// so changing the language takes effect for newly opened windows and the rebuilt tray menu.
/// </summary>
public static class Loc
{
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
        [("en", "English"), ("ru", "Русский"), ("uk", "Українська")];

    public static string Current { get; private set; } = "en";

    public static void SetLanguage(string? code) =>
        Current = Languages.Any(l => l.Code == code) ? code! : "en";

    public static string T(string key)
    {
        if (!Strings.TryGetValue(key, out var s)) return key;
        return Current switch { "ru" => s.Ru, "uk" => s.Uk, _ => s.En };
    }

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    private static readonly Dictionary<string, (string En, string Ru, string Uk)> Strings = new()
    {
        // Tray
        ["tray.history"] = ("Clipboard history", "История буфера", "Історія буфера"),
        ["tray.settings"] = ("Settings…", "Настройки…", "Налаштування…"),
        ["tray.keepAwake"] = ("Prevent screen lock", "Не блокировать экран", "Не блокувати екран"),
        ["tray.exit"] = ("Exit", "Выход", "Вихід"),

        // History popup
        ["history.title"] = ("Typlo — clipboard history", "Typlo — история буфера", "Typlo — історія буфера"),
        ["history.search"] = ("Search history…", "Поиск в истории…", "Пошук в історії…"),
        ["history.hints"] = ("↑↓ select · Enter paste · Shift+Enter plain text · Ctrl+1…9 quick · Ctrl+P pin · Del delete · Esc",
            "↑↓ выбор · Enter вставить · Shift+Enter без оформления · Ctrl+1…9 быстро · Ctrl+P закрепить · Del удалить · Esc",
            "↑↓ вибір · Enter вставити · Shift+Enter без оформлення · Ctrl+1…9 швидко · Ctrl+P закріпити · Del видалити · Esc"),
        ["history.files"] = ("{0} files", "Файлов: {0}", "Файлів: {0}"),
        ["history.file"] = ("File", "Файл", "Файл"),
        ["history.more"] = ("and {0} more", "и ещё {0}", "і ще {0}"),
        ["history.formatted"] = ("formatted", "с оформлением", "з оформленням"),
        ["tray.pause"] = ("Pause history", "Приостановить историю", "Призупинити історію"),
        ["tray.pause10"] = ("For 10 minutes", "На 10 минут", "На 10 хвилин"),
        ["tray.pause60"] = ("For 1 hour", "На 1 час", "На 1 годину"),
        ["tray.pauseRestart"] = ("Until Typlo restarts", "До перезапуска Typlo", "До перезапуску Typlo"),
        ["tray.resumeUntil"] = ("▶ Resume history (paused until {0:HH:mm})", "▶ Возобновить историю (пауза до {0:HH:mm})",
            "▶ Відновити історію (пауза до {0:HH:mm})"),
        ["tray.resumeRestart"] = ("▶ Resume history (paused until restart)", "▶ Возобновить историю (пауза до перезапуска)",
            "▶ Відновити історію (пауза до перезапуску)"),
        ["settings.lastWord"] = ("When nothing is selected, convert the last typed word",
            "Если ничего не выделено, переводить последнее набранное слово",
            "Якщо нічого не виділено, перекладати останнє набране слово"),
        ["history.empty"] = ("Nothing yet — copy something", "Пока пусто — скопируйте что-нибудь", "Поки порожньо — скопіюйте щось"),
        ["history.notFound"] = ("Nothing found", "Ничего не найдено", "Нічого не знайдено"),
        ["history.image"] = ("Image {0}×{1}", "Изображение {0}×{1}", "Зображення {0}×{1}"),
        ["history.imageSearch"] = ("image picture", "изображение картинка", "зображення картинка"),
        ["history.lines"] = ("{0}+ lines · {1}", "{0}+ строк · {1}", "{0}+ рядків · {1}"),
        ["history.bytes"] = ("{0} B", "{0} Б", "{0} Б"),
        ["history.kb"] = ("{0:0.#} KB", "{0:0.#} КБ", "{0:0.#} КБ"),
        ["history.justNow"] = ("just now", "только что", "щойно"),
        ["history.minAgo"] = ("{0} min ago", "{0} мин назад", "{0} хв тому"),
        ["history.hAgo"] = ("{0} h ago", "{0} ч назад", "{0} год тому"),
        ["history.dAgo"] = ("{0} d ago", "{0} дн назад", "{0} дн тому"),

        // Settings
        ["settings.title"] = ("Typlo — settings", "Typlo — настройки", "Typlo — налаштування"),
        ["settings.cancel"] = ("Cancel", "Отмена", "Скасувати"),
        ["settings.save"] = ("Save", "Сохранить", "Зберегти"),
        ["settings.system"] = ("System", "Система", "Система"),
        ["settings.language"] = ("Interface language", "Язык интерфейса", "Мова інтерфейсу"),
        ["settings.autostart"] = ("Add to autostart", "Добавить в автозапуск", "Додати до автозапуску"),
        ["settings.keepAwake"] = ("Prevent screen lock (keep the display on, no sleep)",
            "Не блокировать экран (не гасить и не засыпать)", "Не блокувати екран (не гасити й не засинати)"),
        ["settings.keepAwakeHint"] = ("Typlo tells the system the screen is in use, like during video playback, and keeps your status active in Teams and similar apps while you are away from the keyboard. Manual locking (Win+L, ⌃⌘Q) still works.",
            "Программа сообщает системе, что экран используется, как при просмотре видео, и сохраняет статус «В сети» в Teams и похожих программах, пока вы не трогаете клавиатуру. Ручная блокировка (Win+L, ⌃⌘Q) по-прежнему работает.",
            "Програма повідомляє системі, що екран використовується, як під час перегляду відео, і зберігає статус «У мережі» в Teams та схожих програмах, поки ви не торкаєтеся клавіатури. Ручне блокування (Win+L, ⌃⌘Q) працює як і раніше."),
        ["settings.layoutHeader"] = ("Fix the layout of selected text", "Смена раскладки выделенного текста", "Зміна розкладки виділеного тексту"),
        ["settings.layoutHint"] = ("Select text typed in the wrong layout and press a shortcut: “ghbdtn” → “привет”. Click a field and press a new shortcut; Backspace disables it.",
            "Выделите текст, набранный не в той раскладке, и нажмите сочетание: «ghbdtn» → «привет». Кликните в поле и нажмите новое сочетание; Backspace — отключить.",
            "Виділіть текст, набраний не в тій розкладці, і натисніть сполучення: «ghbdtn» → «привіт». Клацніть у поле й натисніть нове сполучення; Backspace — вимкнути."),
        ["settings.switchLayout"] = ("Switch the system keyboard layout to the new language after converting",
            "После конвертации переключать системную раскладку на новый язык",
            "Після конвертації перемикати системну розкладку на нову мову"),
        ["settings.pair"] = ("Toggle pair", "Пара для переключения", "Пара для перемикання"),
        ["settings.toggle"] = ("Toggle ⇄", "Переключить ⇄", "Перемкнути ⇄"),
        ["settings.convertTo"] = ("Convert to {0}", "Перевести на {0}", "Перекласти на {0}"),
        ["settings.historyHeader"] = ("Clipboard history", "История буфера обмена", "Історія буфера обміну"),
        ["settings.historyEnabled"] = ("Keep history", "Сохранять историю", "Зберігати історію"),
        ["settings.openHistory"] = ("Open history", "Открыть историю", "Відкрити історію"),
        ["settings.maxItems"] = ("Maximum items", "Максимум записей", "Максимум записів"),
        ["settings.maxTotal"] = ("Total size, MB", "Общий размер, МБ", "Загальний розмір, МБ"),
        ["settings.maxImage"] = ("Max image, MB", "Макс. картинка, МБ", "Макс. зображення, МБ"),
        ["settings.excluded"] = ("Never store from apps (process names, comma-separated)",
            "Не сохранять из программ (имена процессов через запятую)",
            "Не зберігати з програм (імена процесів через кому)"),
        ["settings.clear"] = ("Clear history…", "Очистить историю…", "Очистити історію…"),
        ["settings.storage"] = ("History is stored next to the app and encrypted (AES-256-GCM; {0}).",
            "История хранится рядом с программой и зашифрована (AES-256-GCM; {0}).",
            "Історія зберігається поруч із програмою і зашифрована (AES-256-GCM; {0})."),
        ["settings.stats"] = ("Now: {0} items, {1:0.0} MB", "Сейчас: {0} записей, {1:0.0} МБ", "Зараз: {0} записів, {1:0.0} МБ"),
        ["settings.failed"] = ("Could not register: {0}", "Не удалось занять: {0}", "Не вдалося зайняти: {0}"),
        ["settings.languageNote"] = ("The language applies to newly opened windows.", "Язык применяется к новым окнам.", "Мова застосовується до нових вікон."),
        ["hotkey.placeholder"] = ("Press a shortcut…", "Нажмите сочетание…", "Натисніть сполучення…"),
        ["hotkey.off"] = ("off", "выключено", "вимкнено"),
        ["hotkey.needMod"] = ("needs Ctrl/Alt/Win", "нужен Ctrl/Alt/Win", "потрібен Ctrl/Alt/Win"),
        ["hotkey.dup"] = ("duplicate", "повтор", "повтор"),
        ["hotkey.free"] = ("✓ free", "✓ свободно", "✓ вільно"),
        ["hotkey.taken"] = ("taken", "занято", "зайнято"),
        ["clear.title"] = ("Clear history", "Очистить историю", "Очистити історію"),
        ["clear.question"] = ("Delete the whole history except pinned items?", "Удалить всю историю, кроме закреплённых записей?",
            "Видалити всю історію, крім закріплених записів?"),
        ["clear.yes"] = ("Clear", "Очистить", "Очистити"),

        ["layout.en"] = ("English", "английский", "англійську"),
        ["layout.ru"] = ("Russian", "русский", "російську"),
        ["layout.uk"] = ("Ukrainian", "украинский", "українську"),

        ["update.install"] = ("⬆ Update to v{0}", "⬆ Обновить до v{0}", "⬆ Оновити до v{0}"),
        ["update.downloading"] = ("Downloading update… {0}%", "Загрузка обновления… {0}%", "Завантаження оновлення… {0}%"),
        ["update.failed"] = ("Update failed — open the download page", "Не удалось обновить — открыть страницу загрузки",
            "Не вдалося оновити — відкрити сторінку завантаження"),
        ["update.tooltip"] = ("Typlo — update {0} available", "Typlo — доступно обновление {0}", "Typlo — доступне оновлення {0}"),
        ["settings.version"] = ("Typlo v{0}", "Typlo v{0}", "Typlo v{0}"),

        // Controller hotkey labels
        ["hk.toggle"] = ("Toggle layout", "Переключить раскладку", "Перемкнути розкладку"),
        ["hk.history"] = ("Clipboard history", "История буфера", "Історія буфера"),
        ["hk.to"] = ("To {0}", "На {0}", "На {0}"),

        // Platform notes
        ["key.windows"] = ("key protected by Windows DPAPI — unreadable for other users or on another computer",
            "ключ защищён Windows DPAPI — файл нельзя прочитать под другим пользователем или на другом компьютере",
            "ключ захищено Windows DPAPI — файл не прочитати під іншим користувачем чи на іншому комп’ютері"),
        ["key.mac"] = ("key stored in the macOS Keychain", "ключ хранится в связке ключей macOS (Keychain)", "ключ зберігається у в’язці ключів macOS (Keychain)"),
        ["key.secret"] = ("key stored in the Secret Service (GNOME Keyring / KWallet)", "ключ хранится в Secret Service (GNOME Keyring / KWallet)",
            "ключ зберігається в Secret Service (GNOME Keyring / KWallet)"),
        ["key.file"] = ("key stored in a file readable only by your user", "ключ хранится в файле, доступном только вашему пользователю",
            "ключ зберігається у файлі, доступному лише вашому користувачу"),
        ["note.mac.ax"] = ("Allow Typlo in System Settings → Privacy & Security → Accessibility, then restart it.",
            "Разрешите Typlo в «Системные настройки → Конфиденциальность → Универсальный доступ», затем перезапустите программу.",
            "Дозвольте Typlo в «Системні параметри → Приватність → Універсальний доступ», потім перезапустіть програму."),
        ["note.linux.install"] = ("Install: {0}.", "Установите: {0}.", "Встановіть: {0}."),
        ["note.linux.wayland"] = ("Wayland does not allow global shortcuts: bind them in your desktop settings to “Typlo --history” and “Typlo --convert ru|en|toggle”.",
            "Wayland не разрешает глобальные сочетания: назначьте их в настройках рабочего стола на команды «Typlo --history» и «Typlo --convert ru|en|toggle».",
            "Wayland не дозволяє глобальні сполучення: призначте їх у налаштуваннях робочого столу на команди «Typlo --history» і «Typlo --convert ru|en|toggle»."),
    };
}

/// <summary>XAML: <c>Text="{l:Tr settings.save}"</c>.</summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
