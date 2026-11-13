"""Import the public Typlo v0.5.14 interface strings into the Rust catalog."""
import json
import re
import subprocess
import sys
from pathlib import Path

LANGUAGES = ("en", "ru", "uk", "de", "es", "pt", "fr", "it", "zh", "ja")
TAG = "refs/tags/v0.5.14"
SOURCE = "src/Typlo.App/Services/"
STRING = r'"(?:\\.|[^"\\])*"'
KEY = r'\["([A-Za-z0-9_.-]+)"\]\s*=\s*'

# Text introduced by the Rust interface. Other languages use the English fallback,
# as they do for keys absent from the source catalog.
RUST_LABELS = [
    ("convert.swap", "Поменять местами", "Swap layouts", "Поміняти місцями"),
    ("convert.input", "Исходный текст", "Input text", "Вихідний текст"),
    ("convert.output", "Результат", "Result", "Результат"),
    ("convert.copy", "Копировать результат", "Copy result", "Копіювати результат"),
    ("convert.copied", "Результат скопирован", "Result copied", "Результат скопійовано"),
    ("layouts.cycle", "Цикл раскладок", "Layout cycle", "Цикл розкладок"),
    ("layouts.application", "Раскладки по приложениям", "Layouts by application", "Розкладки за програмами"),
    ("layouts.common", "Общий цикл", "Global cycle", "Загальний цикл"),
    ("layouts.direct", "Прямое преобразование в установленную раскладку", "Convert directly to an installed layout", "Перетворити безпосередньо на встановлену розкладку"),
    ("auto.enabled", "Автозамена раскладки после пробела", "Correct keyboard layout after Space", "Автовиправляти розкладку після пробілу"),
    ("auto.protect", "Защищать команды, пути, адреса и код", "Protect commands, paths, addresses and code", "Захищати команди, шляхи, адреси й код"),
    ("app.enabled", "Активно", "Enabled", "Увімкнено"),
    ("app.process", "Имя процесса", "Process name", "Ім’я процесу"),
    ("app.remove", "Убрать правило", "Remove rule", "Видалити правило"),
    ("app.inherit", "Общие настройки", "Use global settings", "Загальні налаштування"),
    ("app.enable", "Включить", "Enable", "Увімкнути"),
    ("app.disable", "Выключить", "Disable", "Вимкнути"),
    ("app.add", "Добавить приложение", "Add application", "Додати програму"),
    ("app.example", "Например, ms-teams.exe", "For example, ms-teams.exe", "Наприклад, ms-teams.exe"),
    ("app.autoInherit", "Автозамена по общим настройкам", "Autocorrection follows global settings", "Автовиправлення за загальними налаштуваннями"),
    ("app.autoOn", "Автозамена включена", "Autocorrection on", "Автовиправлення ввімкнено"),
    ("app.autoOff", "Автозамена выключена", "Autocorrection off", "Автовиправлення вимкнено"),
    ("app.snippetsInherit", "Сниппеты по общим настройкам", "Snippets follow global settings", "Сніпети за загальними налаштуваннями"),
    ("app.snippetsOn", "Сниппеты включены", "Snippets on", "Сніпети ввімкнено"),
    ("app.snippetsOff", "Сниппеты выключены", "Snippets off", "Сніпети вимкнено"),
    ("snippet.auto", "Автоматически раскрывать сниппеты", "Expand snippets automatically", "Автоматично розгортати сніпети"),
    ("snippet.caret", "Добавьте {cursor} в место, где после вставки должен стоять курсор.", "Add {cursor} where the caret should land after insertion.", "Додайте {cursor} у місце, де після вставлення має стояти курсор."),
    ("snippet.example", "Привет!\n{cursor}", "Hello!\n{cursor}", "Привіт!\n{cursor}"),
    ("shortcuts.heading", "Горячие клавиши", "Keyboard shortcuts", "Гарячі клавіші"),
    ("settings.saved", "Настройки сохранены", "Settings saved", "Налаштування збережено"),
    ("capture.heading", "Скриншоты", "Screenshots", "Знімки екрана"),
    ("capture.rectangle", "Прямоугольник", "Rectangle", "Прямокутник"),
    ("capture.free", "Свободная форма", "Freeform", "Довільна форма"),
    ("history.textLimit", "Текст, КБ", "Text, KB", "Текст, КБ"),
    ("history.copy", "Копировать", "Copy", "Копіювати"),
    ("history.image", "Изображение", "Image", "Зображення"),
    ("history.open", "Открыть", "Open", "Відкрити"),
    ("history.refresh", "Обновить", "Refresh", "Оновити список"),
    ("history.previousCopied", "Предыдущая запись скопирована", "Previous item copied", "Попередній запис скопійовано"),
    ("history.previousMissing", "Предыдущая запись отсутствует", "There is no previous item", "Попереднього запису немає"),
    ("history.resume", "Возобновить", "Resume", "Відновити"),
    ("history.closePreview", "Закрыть предпросмотр", "Close preview", "Закрити попередній перегляд"),
    ("history.copied", "Скопировано в буфер", "Copied to clipboard", "Скопійовано в буфер"),
    ("history.ocrSaved", "Распознанный текст сохранён в истории", "Recognized text saved in history", "Розпізнаний текст збережено в історії"),
    ("backup.heading", "Резервная копия", "Backup", "Резервна копія"),
    ("backup.path", "Путь к файлу .typlo", "Path to the .typlo file", "Шлях до файлу .typlo"),
    ("backup.validate", "Проверить копию", "Verify backup", "Перевірити копію"),
    ("backup.restore", "Восстановить настройки и словарь", "Restore settings and dictionary", "Відновити налаштування та словник"),
    ("backup.saved", "Резервная копия сохранена", "Backup saved", "Резервну копію збережено"),
    ("backup.restored", "Резервная копия восстановлена", "Backup restored", "Резервну копію відновлено"),
    ("backup.summary", "Сниппеты: {0} · Слова: {1} · Исключения: {2} · Пары: {3}", "Snippets: {0} · Words: {1} · Exceptions: {2} · Pairs: {3}", "Сніпети: {0} · Слова: {1} · Винятки: {2} · Пари: {3}"),
    ("dictionary.heading", "Личный словарь", "Personal dictionary", "Особистий словник"),
    ("dictionary.words", "Слова", "Words", "Слова"),
    ("dictionary.word", "Слово", "Word", "Слово"),
    ("dictionary.never", "Никогда не исправлять", "Never correct", "Ніколи не виправляти"),
    ("dictionary.prefix", "Начало слова", "Word prefix", "Початок слова"),
    ("dictionary.all", "Все языки", "All languages", "Усі мови"),
    ("dictionary.empty", "Записей нет", "No entries", "Записів немає"),
    ("dictionary.save", "Сохранить слово", "Save word", "Зберегти слово"),
    ("dictionary.addTitle", "Добавить в словарь", "Add to dictionary", "Додати до словника"),
    ("dictionary.duplicate", "Слово уже есть в словаре", "The word is already in the dictionary", "Слово вже є у словнику"),
    ("dictionary.forms", "Слова через запятую", "Comma-separated words", "Слова через кому"),
    ("dictionary.path", "Путь к JSON", "Path to JSON", "Шлях до JSON"),
    ("field.compatibility", "Совместимость поля", "Field compatibility", "Сумісність поля"),
    ("field.choices", "Варианты раскладки", "Layout choices", "Варіанти розкладки"),
    ("field.noChoices", "Нет вариантов для разрешённых раскладок.", "No dictionary matches in the allowed layouts.", "Немає збігів у дозволених розкладках."),
    ("tools.memory", "Память фонового процесса · рабочий набор {0} МиБ · частная память {1} МиБ", "Resident memory · working set {0} MiB · private {1} MiB", "Пам’ять фонового процесу · робочий набір {0} МіБ · приватна пам’ять {1} МіБ"),
    ("spelling.title", "Проверка орфографии", "Spelling review", "Перевірка правопису"),
    ("spelling.selection", "Проверка выделенного текста", "Review selected text", "Перевірка виділеного тексту"),
    ("spelling.empty", "Ошибок не найдено.", "No spelling issues found.", "Помилок не знайдено."),
    ("spelling.skip", "Пропустить", "Skip", "Пропустити"),
    ("spelling.personal", "Личные исправления", "Personal corrections", "Особисті виправлення"),
    ("spelling.wrong", "Ошибка", "Misspelling", "Помилка"),
    ("spelling.correct", "Исправление", "Correction", "Виправлення"),
    ("spelling.save", "Сохранить пару", "Save pair", "Зберегти пару"),
    ("spelling.saveAs", "Сохранить как пару", "Save as a pair", "Зберегти як пару"),
    ("spelling.apply", "Применить к выделению", "Apply to selection", "Застосувати до виділення"),
    ("common.add", "Добавить", "Add", "Додати"),
    ("common.edit", "Изменить", "Edit", "Змінити"),
    ("common.back", "Назад", "Back", "Назад"),
    ("common.next", "Дальше", "Next", "Далі"),
    ("common.up", "Выше", "Move up", "Вище"),
    ("common.remove", "Убрать", "Remove", "Прибрати"),
    ("update.none", "Новой сборки Typlo Rust нет", "No newer Typlo Rust package is available", "Нової збірки Typlo Rust немає"),
    ("update.installed", "Новая версия запущена", "The new version is running", "Нову версію запущено"),
    ("update.verifying", "Загружаю и проверяю обновление…", "Downloading and verifying the update…", "Завантажую та перевіряю оновлення…"),
]

def source(repository, file):
    return subprocess.check_output(
        ["git", "-C", repository, "show", f"{TAG}:{SOURCE}{file}"],
        encoding="utf-8",
    )

def catalog(repository):
    basic = source(repository, "Loc.cs")
    extra = source(repository, "Loc.Translations.cs")
    result = {}
    for match in re.finditer(KEY + rf"\(\s*({STRING}),\s*({STRING}),\s*({STRING})\s*\)", basic):
        key, *values = match.groups()
        result[key] = {code: json.loads(value) for code, value in zip(LANGUAGES, values)}
    for language, block in zip(
        re.findall(r'\["([a-z]{2})"\]\s*=\s*new\(\)\s*\{', extra),
        re.split(r'\["[a-z]{2}"\]\s*=\s*new\(\)\s*\{', extra)[1:],
    ):
        for key, text in re.findall(KEY + rf"({STRING})", block):
            if key in result:
                result[key][language] = json.loads(text)
    files = subprocess.check_output(
        ["git", "-C", repository, "ls-tree", "-r", "--name-only", TAG, SOURCE], encoding="utf-8"
    ).splitlines()
    for path in files:
        name = path.rsplit("/", 1)[-1]
        if not name.startswith("Loc.") or name == "Loc.Translations.cs":
            continue
        array = rf"\[\s*({STRING}(?:\s*,\s*{STRING}){{9}})\s*\]"
        for key, texts in re.findall(KEY + array, source(repository, name)):
            result[key] = dict(zip(LANGUAGES, (json.loads(text) for text in re.findall(STRING, texts)), strict=True))
    if len(result) < 100 or any(not any(code in row for row in result.values()) for code in LANGUAGES):
        coverage = {code: sum(code in row for row in result.values()) for code in LANGUAGES}
        raise ValueError(f"The source catalog could not be read completely: {coverage}")
    return result

if __name__ == "__main__":
    values = catalog(sys.argv[1])
    values.update({f"rust.{key}": {"en": en, "ru": ru, "uk": uk}
                   for key, ru, en, uk in RUST_LABELS})
    Path(__file__).resolve().parent.parent.joinpath("src", "ui_catalog.json").write_text(
        json.dumps(
            {key: [texts.get(code, texts["en"]) for code in LANGUAGES]
             for key, texts in sorted(values.items())},
            ensure_ascii=False,
            separators=(",", ":"),
        ) + "\n",
        encoding="utf-8",
    )
    print(f"Imported {len(values)} keys for {len(LANGUAGES)} languages from {TAG}")
