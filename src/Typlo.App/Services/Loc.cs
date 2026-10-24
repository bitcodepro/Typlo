using Avalonia.Markup.Xaml;

namespace Typlo.App.Services;

/// <summary>
/// UI strings in English (default), Russian and Ukrainian. Windows read them when created,
/// so changing the language takes effect for newly opened windows and the rebuilt tray menu.
/// </summary>
public static partial class Loc
{
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
        [
            ("en", "English"), ("ru", "Русский"), ("uk", "Українська"), ("de", "Deutsch"), ("es", "Español"),
            ("pt", "Português"), ("fr", "Français"), ("it", "Italiano"), ("zh", "中文（简体）"), ("ja", "日本語"),
        ];

    public static string Current { get; private set; } = "en";

    public static void SetLanguage(string? code) =>
        Current = Languages.Any(l => l.Code == code) ? code! : "en";

    public static string T(string key)
    {
        if (!Strings.TryGetValue(key, out var s)) return key;
        return Current switch
        {
            "en" => s.En,
            "ru" => s.Ru,
            "uk" => s.Uk,
            // Other languages live in Loc.Translations.cs; anything missing falls back to English.
            _ => Extra.TryGetValue(Current, out var table) && table.TryGetValue(key, out var text) ? text : s.En,
        };
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
        ["history.hints"] = ("Enter paste · Shift+Enter plain · Ctrl+T text from image · Ctrl+P pin · Del delete · right click: more",
            "Enter вставить · Shift+Enter без оформления · Ctrl+T текст с картинки · Ctrl+P закрепить · Del удалить · правый клик: ещё",
            "Enter вставити · Shift+Enter без оформлення · Ctrl+T текст із зображення · Ctrl+P закріпити · Del видалити · правий клік: ще"),
        ["history.ctx.paste"] = ("Paste", "Вставить", "Вставити"),
        ["history.ctx.plain"] = ("Paste as plain text", "Вставить без оформления", "Вставити без оформлення"),
        ["history.ctx.ocr"] = ("Copy text from image", "Скопировать текст с картинки", "Скопіювати текст із зображення"),
        ["history.ctx.pin"] = ("Pin", "Закрепить", "Закріпити"),
        ["history.ctx.unpin"] = ("Unpin", "Открепить", "Відкріпити"),
        ["history.ctx.delete"] = ("Delete", "Удалить", "Видалити"),
        ["history.ocrBusy"] = ("Reading text…", "Распознаю текст…", "Розпізнаю текст…"),
        ["history.ocrDone"] = ("✓ Text copied", "✓ Текст скопирован", "✓ Текст скопійовано"),
        ["history.ocrNone"] = ("No text found", "Текст не найден", "Текст не знайдено"),
        ["settings.theme"] = ("Theme", "Тема", "Тема"),
        ["theme.system"] = ("As in the system", "Как в системе", "Як у системі"),
        ["theme.light"] = ("Light", "Светлая", "Світла"),
        ["theme.dark"] = ("Dark", "Тёмная", "Темна"),
        ["settings.export"] = ("Export settings…", "Экспорт настроек…", "Експорт налаштувань…"),
        ["settings.import"] = ("Import settings…", "Импорт настроек…", "Імпорт налаштувань…"),
        ["settings.exported"] = ("Settings saved to the file", "Настройки сохранены в файл", "Налаштування збережено у файл"),
        ["settings.importFailed"] = ("This is not a Typlo settings file", "Это не файл настроек Typlo", "Це не файл налаштувань Typlo"),
        ["settings.snippetsHeader"] = ("Snippets", "Шаблоны", "Шаблони"),
        ["settings.snippetsHint"] = ("Type a trigger anywhere and Typlo replaces it with the text. Start triggers with a symbol such as ; so they never fire inside normal words. Triggers work in any keyboard layout. Snippets are stored unencrypted in settings.json.",
            "Наберите сокращение где угодно, и Typlo заменит его текстом. Начинайте сокращения с символа, например ;, чтобы они не срабатывали внутри обычных слов. Работает в любой раскладке. Шаблоны хранятся в settings.json без шифрования.",
            "Наберіть скорочення будь-де, і Typlo замінить його текстом. Починайте скорочення із символу, наприклад ;, щоб вони не спрацьовували всередині звичайних слів. Працює в будь-якій розкладці. Шаблони зберігаються в settings.json без шифрування."),
        ["settings.snippetTrigger"] = (";addr", ";адрес", ";адреса"),
        ["settings.snippetText"] = ("Text to insert", "Текст для вставки", "Текст для вставлення"),
        ["settings.snippetAdd"] = ("+ Add snippet", "+ Добавить шаблон", "+ Додати шаблон"),
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
        ["settings.layoutHint"] = ("Select text typed in the wrong layout and press a shortcut: “ghbdtn” → “привет”. With nothing selected, the last word is converted. Click a field and press a new shortcut; Backspace disables it.",
            "Выделите текст, набранный не в той раскладке, и нажмите сочетание: «ghbdtn» → «привет». Без выделения переводится последнее слово. Кликните в поле и нажмите новое сочетание; Backspace — отключить.",
            "Виділіть текст, набраний не в тій розкладці, і натисніть сполучення: «ghbdtn» → «привіт». Без виділення перекладається останнє слово. Клацніть у поле й натисніть нове сполучення; Backspace — вимкнути."),
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
        ["shortcuts.menu"] = ("Keyboard shortcuts…", "Сочетания клавиш…", "Сполучення клавіш…"),
        ["shortcuts.title"] = ("Typlo — keyboard shortcuts", "Typlo — сочетания клавиш", "Typlo — сполучення клавіш"),
        ["shortcuts.global"] = ("Anywhere in Windows", "В любой программе", "У будь-якій програмі"),
        ["shortcuts.globalHint"] = ("Text tools work on the selection. With nothing selected, they take the word left of the caret.",
            "Инструменты для текста работают с выделением. Если ничего не выделено, берут слово слева от курсора.",
            "Інструменти для тексту працюють із виділенням. Якщо нічого не виділено, беруть слово ліворуч від курсора."),
        ["shortcuts.toggle"] = ("Fix the layout: switch to the other layout of the pair (ghbdtn → привет)",
            "Исправить раскладку: перевести в другую раскладку пары (ghbdtn → привет)",
            "Виправити розкладку: перекласти в іншу розкладку пари (ghbdtn → привіт)"),
        ["shortcuts.case"] = ("Change case: lower → Title → UPPER", "Сменить регистр: нижний → С заглавной → ВЕРХНИЙ",
            "Змінити регістр: нижній → З великої → ВЕРХНІЙ"),
        ["shortcuts.translit"] = ("Transliterate: Привет ⇄ Privet", "Транслитерация: Привет ⇄ Privet", "Транслітерація: Привіт ⇄ Pryvit"),
        ["shortcuts.history"] = ("Open the clipboard history", "Открыть историю буфера", "Відкрити історію буфера"),
        ["shortcuts.previous"] = ("Press again while it is open: paste the previous item", "Ещё раз, пока окно открыто: вставить предыдущий элемент",
            "Ще раз, поки вікно відкрите: вставити попередній елемент"),
        ["shortcuts.historyWindow"] = ("In the history window", "В окне истории", "У вікні історії"),
        ["shortcuts.select"] = ("Select an item", "Выбрать запись", "Вибрати запис"),
        ["shortcuts.quick"] = ("Paste item 1…9 at once", "Сразу вставить запись 1…9", "Одразу вставити запис 1…9"),
        ["shortcuts.typing"] = ("Just type", "Просто печатайте", "Просто друкуйте"),
        ["shortcuts.search"] = ("Search the history", "Поиск по истории", "Пошук в історії"),
        ["shortcuts.snippets"] = ("Type a trigger anywhere: it is replaced with its text", "Наберите сокращение где угодно: оно заменится текстом",
            "Наберіть скорочення будь-де: воно заміниться текстом"),
        ["shortcuts.change"] = ("Change shortcuts…", "Изменить сочетания…", "Змінити сполучення…"),
        ["update.check"] = ("Check for updates…", "Проверить обновления…", "Перевірити оновлення…"),
        ["update.dialogTitle"] = ("Typlo updates", "Обновления Typlo", "Оновлення Typlo"),
        ["update.checking"] = ("Checking for updates…", "Проверяю обновления…", "Перевіряю оновлення…"),
        ["update.current"] = ("Installed version: v{0}", "Установлена версия v{0}", "Встановлена версія v{0}"),
        ["update.upToDate"] = ("You have the latest version", "У вас последняя версия", "У вас остання версія"),
        ["update.available"] = ("Typlo v{0} is available", "Доступна Typlo v{0}", "Доступна Typlo v{0}"),
        ["update.availableDetails"] = ("You have v{0}. Typlo will download v{1}, check it, replace itself and restart. Your history and settings stay.",
            "У вас v{0}. Typlo скачает v{1}, проверит её, заменит себя и перезапустится. История и настройки сохранятся.",
            "У вас v{0}. Typlo завантажить v{1}, перевірить її, замінить себе й перезапуститься. Історія та налаштування збережуться."),
        ["update.installNow"] = ("Update now", "Обновить", "Оновити"),
        ["update.later"] = ("Later", "Позже", "Пізніше"),
        ["update.close"] = ("Close", "Закрыть", "Закрити"),
        ["update.retry"] = ("Try again", "Повторить", "Повторити"),
        ["update.openPage"] = ("Open download page", "Открыть страницу загрузки", "Відкрити сторінку завантаження"),
        ["update.checkFailed"] = ("Could not check for updates", "Не удалось проверить обновления", "Не вдалося перевірити оновлення"),
        ["update.noConnection"] = ("No connection to GitHub.", "Нет связи с GitHub.", "Немає зв’язку з GitHub."),
        ["update.devBuild"] = ("Updates are off for developer builds", "В сборке для разработки обновления отключены", "У збірці для розробки оновлення вимкнено"),
        ["settings.version"] = ("Typlo v{0}", "Typlo v{0}", "Typlo v{0}"),

        // Controller hotkey labels
        ["hk.case"] = ("Change case", "Сменить регистр", "Змінити регістр"),
        ["hk.translit"] = ("Transliterate", "Транслитерация", "Транслітерація"),
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
