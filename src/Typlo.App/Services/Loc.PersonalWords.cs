namespace Typlo.App.Services;

public static partial class Loc
{
    private static void AddPersonalWordStrings()
    {
        var strings = new Dictionary<string, string[]>
        {
            ["dictionary.title"] = ["Personal dictionary", "Личный словарь", "Особистий словник", "Persönliches Wörterbuch", "Diccionario personal", "Dicionário pessoal", "Dictionnaire personnel", "Dizionario personale", "个人词典", "個人辞書"],
            ["dictionary.hint"] = ["Add the exact form you type. “Write”, “writes” and “wrote” are separate entries. Choose the language of the correctly spelled word. Updates keep your words.", "Добавляй точную форму: «писать», «напиши», «написал» — отдельные записи. Выбери язык правильно написанного слова. Обновления сохраняют твои слова.", "Додавай точну форму: «писати», «напиши», «написав» — окремі записи. Вибери мову правильно написаного слова. Оновлення зберігають твої слова.", "Die genaue Wortform hinzufügen. Jede Form ist ein eigener Eintrag. Die Sprache des richtig geschriebenen Wortes wählen. Updates behalten deine Wörter.", "Añade la forma exacta que escribes. Cada forma es una entrada. Elige el idioma de la palabra correcta. Las actualizaciones conservan tus palabras.", "Adicione a forma exata que digita. Cada forma é uma entrada. Escolha o idioma da palavra correta. As atualizações mantêm suas palavras.", "Ajoutez la forme exacte que vous tapez. Chaque forme est une entrée distincte. Choisissez la langue du mot correct. Les mises à jour conservent vos mots.", "Aggiungi la forma esatta che scrivi. Ogni forma è una voce distinta. Scegli la lingua della parola corretta. Gli aggiornamenti conservano le tue parole.", "添加实际输入的正确词形；不同词形需分别添加。选择单词的语言。更新会保留个人词条。", "実際に入力する正しい語形を追加します。語形ごとに登録し、単語の言語を選んでください。更新後も保持されます。"],
            ["dictionary.word"] = ["Correct word", "Правильное слово", "Правильне слово", "Richtiges Wort", "Palabra correcta", "Palavra correta", "Mot correct", "Parola corretta", "正确单词", "正しい単語"],
            ["dictionary.language"] = ["Word language", "Язык слова", "Мова слова", "Sprache des Wortes", "Idioma de la palabra", "Idioma da palavra", "Langue du mot", "Lingua della parola", "单词语言", "単語の言語"],
            ["dictionary.add"] = ["Add word", "Добавить слово", "Додати слово", "Wort hinzufügen", "Añadir palabra", "Adicionar palavra", "Ajouter le mot", "Aggiungi parola", "添加单词", "単語を追加"],
            ["dictionary.remove"] = ["Remove personal entry", "Удалить личную запись", "Видалити особистий запис", "Eigenen Eintrag entfernen", "Eliminar entrada personal", "Remover entrada pessoal", "Supprimer l’entrée personnelle", "Rimuovi voce personale", "删除个人词条", "個人登録を削除"],
            ["dictionary.invalid"] = ["Choose a language and enter one word: letters, internal apostrophes or hyphens.", "Выбери язык и введи одно слово: буквы, апостроф или дефис внутри.", "Вибери мову й введи одне слово: літери, апостроф або дефіс усередині.", "Sprache wählen und ein Wort eingeben: Buchstaben, innere Apostrophe oder Bindestriche.", "Elige un idioma y una palabra: letras, apóstrofos o guiones internos.", "Escolha um idioma e uma palavra: letras, apóstrofos ou hífens internos.", "Choisissez une langue et un mot : lettres, apostrophes ou traits d’union internes.", "Scegli una lingua e una parola: lettere, apostrofi o trattini interni.", "请选择语言并输入一个单词：字母及内部的撇号或连字符。", "言語を選び、文字と語中のアポストロフィ・ハイフンで単語を入力してください。"],
        };
        foreach (var (key, values) in strings)
        {
            Strings[key] = (values[0], values[1], values[2]);
            for (var i = 3; i < Languages.Count; i++) Extra[Languages[i].Code][key] = values[i];
        }
    }
}
