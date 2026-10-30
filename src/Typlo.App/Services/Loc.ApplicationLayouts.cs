namespace Typlo.App.Services;

public static partial class Loc
{
    private static void AddApplicationLayoutStrings()
    {
        var strings = new Dictionary<string, string[]>
        {
            ["appLayouts.title"] = ["Layouts for applications", "Раскладки для приложений", "Розкладки для застосунків", "Layouts für Anwendungen", "Distribuciones por aplicación", "Layouts por aplicativo", "Dispositions par application", "Layout per applicazione", "应用程序布局", "アプリごとの配列"],
            ["appLayouts.hint"] = ["Keep an allowed layout on entry. Otherwise use English if selected, or the first layout. An empty list uses the global cycle.", "При входе сохраняется разрешённая раскладка. Иначе включается английская, если выбрана, или первая в списке. Пустой список означает общий цикл.", "При вході зберігається дозволена розкладка. Інакше вмикається англійська, якщо вибрана, або перша у списку. Порожній список означає загальний цикл.", "Beim App-Wechsel bleibt ein erlaubtes Layout aktiv. Sonst gilt Englisch, falls ausgewählt, oder das erste Layout. Eine leere Liste nutzt den allgemeinen Zyklus.", "Al entrar se conserva una distribución permitida. Si no, se activa inglés si está seleccionado, o la primera. Una lista vacía usa el ciclo general.", "Ao entrar, mantém um layout permitido. Caso contrário, usa inglês se selecionado, ou o primeiro layout. Uma lista vazia usa o ciclo geral.", "À l’entrée, conserve une disposition autorisée. Sinon, active l’anglais s’il est sélectionné, ou la première disposition. Une liste vide utilise le cycle global.", "All’ingresso mantiene un layout consentito. Altrimenti usa l’inglese se selezionato, o il primo layout. Un elenco vuoto usa il ciclo globale.", "进入应用时保留已允许的布局。否则优先使用已选英语布局，没有则使用列表中的第一个。空列表使用全局循环。", "切り替え時、許可された配列は維持します。対象外なら選択済みの英語、それがなければ先頭の配列を使います。空の一覧では全体の順序を使います。"],
            ["appLayouts.add"] = ["Add application", "Добавить приложение", "Додати застосунок", "Anwendung hinzufügen", "Añadir aplicación", "Adicionar aplicativo", "Ajouter une application", "Aggiungi applicazione", "添加应用", "アプリを追加"],
            ["appLayouts.process"] = ["Process name, e.g. ms-teams.exe", "Имя процесса, например ms-teams.exe", "Назва процесу, наприклад ms-teams.exe", "Prozessname, z. B. ms-teams.exe", "Proceso, p. ej. ms-teams.exe", "Processo, ex.: ms-teams.exe", "Processus, ex. ms-teams.exe", "Processo, es. ms-teams.exe", "进程名，例如 ms-teams.exe", "プロセス名（例: ms-teams.exe）"],
            ["appLayouts.enabled"] = ["Enabled", "Включено", "Увімкнено", "Aktiviert", "Activado", "Ativado", "Activé", "Attivo", "启用", "有効"],
            ["appLayouts.choose"] = ["Choose process", "Выбрать процесс", "Вибрати процес", "Prozess auswählen", "Elegir proceso", "Escolher processo", "Choisir un processus", "Scegli processo", "选择进程", "プロセスを選択"],
            ["appLayouts.search"] = ["Search running processes", "Поиск запущенных процессов", "Пошук запущених процесів", "Laufende Prozesse suchen", "Buscar procesos activos", "Buscar processos ativos", "Rechercher les processus actifs", "Cerca processi attivi", "搜索正在运行的进程", "実行中のプロセスを検索"],
            ["appLayouts.refresh"] = ["Refresh processes", "Обновить процессы", "Оновити процеси", "Prozesse aktualisieren", "Actualizar procesos", "Atualizar processos", "Actualiser les processus", "Aggiorna processi", "刷新进程", "プロセスを更新"],
            ["appLayouts.invalid"] = ["Enter a process name and select at least one layout.", "Укажите процесс и выберите хотя бы одну раскладку.", "Укажіть процес і виберіть хоча б одну розкладку.", "Prozessnamen und mindestens ein Layout wählen.", "Indica un proceso y al menos una distribución.", "Informe um processo e selecione ao menos um layout.", "Indiquez un processus et au moins une disposition.", "Indica un processo e almeno un layout.", "请输入进程名并选择至少一种布局。", "プロセス名と1つ以上の配列を指定してください。"],
            ["appLayouts.duplicate"] = ["A rule for {0} already exists.", "Правило для {0} уже существует.", "Правило для {0} вже існує.", "Eine Regel für {0} ist bereits vorhanden.", "Ya existe una regla para {0}.", "Já existe uma regra para {0}.", "Une règle pour {0} existe déjà.", "Esiste già una regola per {0}.", "已存在 {0} 的规则。", "{0} のルールは既にあります。"],
            ["appLayouts.missing"] = ["Some layouts are unavailable. This rule will use the global cycle until all are available.", "Некоторые раскладки недоступны. Пока они не появятся, применяется общий цикл.", "Деякі розкладки недоступні. Поки вони не з’являться, застосовується загальний цикл.", "Einige Layouts fehlen. Bis alle verfügbar sind, gilt der allgemeine Zyklus.", "Faltan distribuciones. Se usará el ciclo general hasta que estén disponibles.", "Alguns layouts estão indisponíveis. O ciclo geral será usado até que todos estejam disponíveis.", "Des dispositions sont indisponibles. Le cycle général s’applique en attendant.", "Alcuni layout non sono disponibili. Nel frattempo si usa il ciclo generale.", "部分布局不可用。所有布局可用前，将使用全局循环。", "一部の配列が使えません。すべて揃うまでは全体の順序を使います。"],
            ["appLayouts.single"] = ["On entry: {0}", "При входе: {0}", "При вході: {0}", "Beim App-Wechsel: {0}", "Al entrar: {0}", "Ao entrar: {0}", "À l’entrée : {0}", "All’ingresso: {0}", "进入时：{0}", "切り替え時: {0}"],
        };
        foreach (var (key, values) in strings)
        {
            Strings[key] = (values[0], values[1], values[2]);
            for (var i = 3; i < Languages.Count; i++) Extra[Languages[i].Code][key] = values[i];
        }
    }
}
