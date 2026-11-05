namespace Typlo.App.Services;

public static partial class Loc
{
    private static void AddToolsStrings()
    {
        var labels = new Dictionary<string, (string En, string Ru, string Uk)>
        {
            ["spell.title"] = ("Check spelling", "Проверить орфографию", "Перевірити правопис"),
            ["spell.quick"] = ("Correct selected text without a window", "Исправить выделенный текст без окна", "Виправити виділений текст без вікна"),
            ["spell.pairs"] = ("My corrections", "Мои исправления", "Мої виправлення"),
            ["spell.pairsHint"] = ("Exact word pairs take priority over dictionary suggestions. Each side is one word (2–64 letters, internal apostrophes/hyphens). Exceptions stay protected. Pairs are included in settings backups.", "Пары для отдельных слов имеют приоритет над предложениями словаря. С каждой стороны — одно слово (2–64 буквы, допустимы внутренние апострофы/дефисы). Исключения сохраняются. Пары включаются в резервную копию настроек.", "Пари для окремих слів мають пріоритет над пропозиціями словника. З кожного боку — одне слово (2–64 літери, можливі внутрішні апострофи/дефіси). Винятки зберігаються. Пари входять до резервної копії налаштувань."),
            ["spell.pairError"] = ("Typed word", "Написанное слово", "Написане слово"),
            ["spell.pairCorrection"] = ("Replace with", "Заменять на", "Замінювати на"),
            ["spell.pairNew"] = ("New pair", "Новая пара", "Нова пара"),
            ["spell.pairSave"] = ("Save pair", "Сохранить пару", "Зберегти пару"),
            ["spell.pairSaved"] = ("Saved. Used on the next check.", "Сохранено. Применится при следующей проверке.", "Збережено. Застосується під час наступної перевірки."),
            ["spell.pairInvalid"] = ("Enter two different words, 2–64 letters each, with internal apostrophes/hyphens only.", "Введи два разных слова по 2–64 буквы, допустимы внутренние апострофы/дефисы.", "Введи два різні слова по 2–64 літери, можливі внутрішні апострофи/дефіси."),
            ["spell.pairDuplicate"] = ("A correction for this word already exists. Select it to edit.", "Исправление для этого слова уже есть. Выбери его для редактирования.", "Виправлення для цього слова вже є. Вибери його для редагування."),
            ["spell.pairStale"] = ("This pair changed or was removed. Reopen the window.", "Пара была изменена или удалена. Открой окно заново.", "Пару було змінено або видалено. Відкрий вікно знову."),
            ["spell.pairLimit"] = ("The limit is 1,000 correction pairs.", "Допускается не более 1000 пар исправлений.", "Допускається не більше 1000 пар виправлень."),
            ["spell.copy"] = ("Copy corrected text", "Скопировать результат", "Скопіювати результат"),
            ["spell.copied"] = ("Corrected text copied.", "Результат скопирован.", "Результат скопійовано."),
            ["spell.copyOnly"] = ("Safe replacement is unavailable in this environment. Copy the result and paste it into the intended field yourself.", "В этом окружении безопасная замена недоступна. Скопируй результат и вставь его в нужное поле вручную.", "У цьому середовищі безпечна заміна недоступна. Скопіюй результат і встав його в потрібне поле вручну."),
            ["spell.selection"] = ("Select text in your editor, then press the spelling shortcut again.", "Выдели текст в редакторе и нажми сочетание проверки орфографии ещё раз.", "Виділи текст у редакторі й натисни сполучення перевірки правопису ще раз."),
            ["spell.limit"] = ("Select a smaller fragment (up to 20,000 characters).", "Выдели меньший фрагмент (до 20 000 символов).", "Виділи менший фрагмент (до 20 000 символів)."),
            ["spell.hint"] = ("Offline spelling for selected plain text (up to 20,000 characters). Choose corrections below; the original is kept by default. Apply replaces only the unchanged source selection. Formatting may be removed.", "Проверка выделенного текста офлайн (до 20 000 символов). Выбери исправления ниже; по умолчанию слова сохраняются. Применение заменит только неизменённое исходное выделение. Оформление может быть потеряно.", "Перевірка виділеного тексту офлайн (до 20 000 символів). Вибери виправлення нижче; за замовчуванням слова зберігаються. Застосування замінить лише незмінене початкове виділення. Форматування може бути втрачено."),
            ["spell.cancel"] = ("Cancel", "Отмена", "Скасувати"),
            ["spell.apply"] = ("Apply corrections", "Применить исправления", "Застосувати виправлення"),
            ["spell.keep"] = ("Keep original", "Оставить как есть", "Залишити як є"),
            ["spell.busy"] = ("Checking spelling…", "Проверяю орфографию…", "Перевіряю правопис…"),
            ["spell.clean"] = ("No unknown words found.", "Неизвестных слов не найдено.", "Невідомих слів не знайдено."),
            ["spell.found"] = ("Words to review: {0}. Choose a suggestion or keep the original. At most 300 words are shown.", "Слов для проверки: {0}. Выбери вариант или оставь исходное слово. Показано не более 300 слов.", "Слів для перевірки: {0}. Вибери варіант або залиш початкове слово. Показано не більше 300 слів."),
            ["spell.changed"] = ("The source selection changed or expired. Select the text and check again.", "Исходное выделение изменилось или устарело. Выдели текст и запусти проверку заново.", "Початкове виділення змінилося або застаріло. Виділи текст і запусти перевірку знову."),
            ["spell.failed"] = ("Spelling check failed or timed out. Check that the spelling folder was extracted with the application.", "Не удалось проверить орфографию или истекло время ожидания. Проверь, что папка spelling распакована вместе с приложением.", "Не вдалося перевірити правопис або сплив час очікування. Перевір, що папку spelling розпаковано разом із програмою."),
            ["tools.measurements"] = ("Field and measurements", "Поле и замеры", "Поле й виміри"),
            ["tools.backupInvalid"] = ("The backup is invalid, unsupported or changed after preview. Select a valid .typlo file and inspect it again.", "Резервная копия повреждена, не поддерживается или изменилась после просмотра. Выбери корректный файл .typlo и проверь его заново.", "Резервна копія пошкоджена, не підтримується або змінилася після перегляду. Вибери коректний файл .typlo й перевір його знову."),
            ["tools.backupGroup"] = ("Backup and reports", "Резервная копия и отчёты", "Резервна копія та звіти"),
            ["tools.protect"] = ("Protect code, paths and commands (shell auto-correction off unless enabled by profile)", "Защищать код, пути и команды (в терминалах автозамена выключена, если не включена профилем)", "Захищати код, шляхи й команди (у терміналах автозаміну вимкнено, якщо не увімкнено профілем)"),
            ["tools.snippets"] = ("Enable snippets", "Включить сниппеты", "Увімкнути сніпети"),
            ["tools.title"] = ("Tools, performance and backup", "Инструменты, замеры и резервная копия", "Інструменти, виміри й резервна копія"),
            ["tools.choices"] = ("Choose a spelling", "Выбрать вариант слова", "Вибрати варіант слова"),
            ["tools.accept"] = ("Apply selected spelling", "Применить вариант", "Застосувати варіант"),
            ["tools.cancel"] = ("Dismiss spelling choices", "Закрыть варианты", "Закрити варіанти"),
            ["tools.profileAuto"] = ("Auto-correction: checked On, unchecked Off, dash Inherit", "Автозамена: ✓ включена, пусто выключена, − наследовать", "Автозаміна: ✓ увімкнено, порожньо вимкнено, − успадкувати"),
            ["tools.profileSnippets"] = ("Snippets: checked On, unchecked Off, dash Inherit", "Сниппеты: ✓ включены, пусто выключены, − наследовать", "Сніпети: ✓ увімкнено, порожньо вимкнено, − успадкувати"),
            ["tools.field"] = ("Application: {0}\nDirect access: {1}\nSelection: {2}\nFallback input: {3}", "Приложение: {0}\nПрямой доступ: {1}\nВыделение: {2}\nРезервный ввод: {3}", "Програма: {0}\nПрямий доступ: {1}\nВиділення: {2}\nРезервне введення: {3}"),
            ["tools.directReady"] = ("reading available; write not tested", "чтение доступно; запись не проверялась", "читання доступне; запис не перевірявся"),
            ["tools.directNo"] = ("unavailable for this field", "недоступен для этого поля", "недоступний для цього поля"),
            ["tools.selectionPossible"] = ("input supported; editor acceptance not tested", "ввод поддерживается; приём редактором не проверялся", "введення підтримується; приймання редактором не перевірялося"),
            ["tools.selectionNo"] = ("unavailable in this OS session", "недоступно в этой сессии ОС", "недоступно в цій сесії ОС"),
            ["tools.unicode"] = ("Unicode keyboard input", "ввод Unicode с клавиатуры", "введення Unicode з клавіатури"),
            ["tools.clipboard"] = ("clipboard", "буфер обмена", "буфер обміну"),
            ["tools.noWord"] = ("Type a word, keep the caret there, then use Choose spelling.", "Напечатай слово, оставь курсор на месте и вызови выбор варианта.", "Напиши слово, залиш курсор на місці й виклич вибір варіанта."),
            ["tools.noOptions"] = ("No dictionary candidates for this word. Manual layout conversion is still available.", "В словаре нет вариантов этого слова. Ручное преобразование раскладки доступно.", "У словнику немає варіантів цього слова. Ручне перетворення розкладки доступне."),
            ["tools.choiceHint"] = ("{0}: next variant · {1}: apply · {2}: cancel. Typing or moving the caret cancels. Expires in 30 s.", "{0}: следующий вариант · {1}: применить · {2}: отмена. Ввод и движение курсора отменяют выбор. Срок — 30 с.", "{0}: наступний варіант · {1}: застосувати · {2}: скасувати. Введення й рух курсора скасовують вибір. Термін — 30 с."),
            ["tools.probeHint"] = ("Close Settings before using the shortcuts. In the target editor: {3} checks field access without changing text. {0} shows dictionary choices; {1} applies, {2} cancels.", "Перед использованием сочетаний закрой настройки. В нужном редакторе: {3} проверяет доступ к полю без изменения текста. {0} показывает варианты словаря; {1} применяет, {2} отменяет.", "Перед використанням сполучень закрий налаштування. У потрібному редакторі: {3} перевіряє доступ до поля без зміни тексту. {0} показує варіанти словника; {1} застосовує, {2} скасовує."),
            ["tools.noProbe"] = ("No field probe yet. Run its shortcut while the target editor is active.", "Поле ещё не проверялось. Нажми сочетание проверки в нужном редакторе.", "Поле ще не перевірялось. Натисни сполучення перевірки в потрібному редакторі."),
            ["tools.metrics"] = ("Resident memory: working set {0:F1} MiB · private {1:F1} MiB · managed {2:F1} MiB. Text operation median {3} ms · P95 {4} ms.", "Память ядра: рабочий набор {0:F1} МиБ · частная {1:F1} МиБ · .NET {2:F1} МиБ. Медиана операции {3} мс · P95 {4} мс.", "Памʼять ядра: робочий набір {0:F1} МіБ · приватна {1:F1} МіБ · .NET {2:F1} МіБ. Медіана операції {3} мс · P95 {4} мс."),
            ["tools.performanceHint"] = ("Last 50 measurements in RAM. Times include waits and editor response. The report has no words, app names or clipboard contents.", "Последние 50 замеров в памяти. Время включает ожидание и ответ редактора. В отчёте нет слов, имён программ и содержимого буфера.", "Останні 50 вимірів у памʼяті. Час містить очікування й відповідь редактора. У звіті немає слів, назв програм і вмісту буфера."),
            ["tools.backupHint"] = ("Full backup includes applied settings, shortcuts, profiles, snippets, personal words and exceptions. Clipboard history and images are excluded. Keep the backup private.", "Общая копия содержит применённые настройки, шорткаты, профили, сниппеты, личные слова и исключения. История буфера и изображения не входят. Храни файл приватно.", "Загальна копія містить застосовані налаштування, шорткати, профілі, сніпети, особисті слова й винятки. Історія буфера та зображення не входять. Зберігай файл приватно."),
            ["tools.backup"] = ("Export full backup…", "Экспорт общей копии…", "Експорт загальної копії…"),
            ["tools.restore"] = ("Restore backup…", "Восстановить копию…", "Відновити копію…"),
            ["tools.report"] = ("Save performance report…", "Сохранить отчёт скорости…", "Зберегти звіт швидкості…"),
            ["tools.preview"] = ("Replace current settings and personal dictionary? Words: {0}; exceptions: {1}; layouts: {2}; snippets: {3}; profiles: {4}. Unavailable layout IDs may need updating on this computer. After a successful restore, restored retention limits may delete old unpinned clipboard history.", "Заменить текущие настройки и личный словарь? Слов: {0}; исключений: {1}; раскладок: {2}; сниппетов: {3}; профилей: {4}. Недоступные на этом компьютере раскладки потребуется настроить. После успешного восстановления новые лимиты могут удалить старую незакреплённую историю буфера.", "Замінити поточні налаштування й особистий словник? Слів: {0}; винятків: {1}; розкладок: {2}; сніпетів: {3}; профілів: {4}. Недоступні на цьому компʼютері розкладки потрібно налаштувати. Після успішного відновлення нові ліміти можуть видалити стару незакріплену історію буфера."),
        };
        foreach (var (key, value) in labels)
        {
            Strings[key] = value;
            // New tools retain English fallback until the other interface translations are supplied.
            foreach (var language in Languages.Skip(3)) Extra[language.Code][key] = value.En;
        }
    }
}
