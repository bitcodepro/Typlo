namespace Typlo.App.Services;

public static partial class Loc
{
    private static void AddPaintStrings()
    {
        var strings = new Dictionary<string, string[]>
        {
            ["capture.settingsHint"] = ["Choose a shape and an action. Copy puts the original on the clipboard; Save opens the editor before saving a JPEG. Configure shortcuts below.", "Выберите форму и действие. Копирование отправит оригинал в буфер; сохранение откроет редактор перед записью JPEG. Сочетания клавиш — ниже.", "Виберіть форму та дію. Копіювання додасть оригінал у буфер; збереження відкриє редактор перед записом JPEG. Сполучення клавіш — нижче.", "Form und Aktion wählen. Kopieren legt das Original in die Zwischenablage; Speichern öffnet den Editor vor dem JPEG-Export. Kürzel unten ändern.", "Elige forma y acción. Copiar envía el original al portapapeles; Guardar abre el editor antes de guardar JPEG. Atajos abajo.", "Escolha forma e ação. Copiar envia o original à área de transferência; Salvar abre o editor antes de salvar JPEG. Atalhos abaixo.", "Choisissez forme et action. Copier place l’original dans le presse-papiers ; Enregistrer ouvre l’éditeur avant le JPEG. Raccourcis ci-dessous.", "Scegli forma e azione. Copia invia l’originale agli appunti; Salva apre l’editor prima del JPEG. Scorciatoie sotto.", "选择形状和操作。复制将原图放入剪贴板；保存将先打开编辑器，再保存 JPEG。可在下方设置快捷键。", "形状と操作を選択します。コピーは元画像をクリップボードへ送ります。保存はJPEG保存の前に編集画面を開きます。下でキーを設定できます。"],
            ["paint.title"] = ["Screenshot editor", "Редактор скриншота", "Редактор знімка", "Screenshot-Editor", "Editor de capturas", "Editor de capturas", "Éditeur de capture", "Editor schermate", "截图编辑器", "スクリーンショット編集"],
            ["paint.limit"] = ["Drawing limit reached. Undo or clear drawings to continue.", "Достигнут предел пометок. Отмените действие или уберите рисунки.", "Досягнуто межі позначок. Скасуйте дію або приберіть малюнки.", "Zeichenlimit erreicht. Rückgängig machen oder Zeichnungen löschen.", "Límite alcanzado. Deshaz o borra dibujos.", "Limite atingido. Desfaça ou limpe os desenhos.", "Limite atteinte. Annulez ou effacez les dessins.", "Limite raggiunto. Annulla o cancella i disegni.", "已达到绘图上限。请撤销或清除绘图。", "描画数の上限です。元に戻すか描画を消去してください。"],
            ["paint.pen"] = ["Pen", "Перо", "Перо", "Stift", "Lápiz", "Caneta", "Stylo", "Penna", "画笔", "ペン"],
            ["paint.marker"] = ["Marker", "Маркер", "Маркер", "Marker", "Resaltador", "Marcador", "Surligneur", "Evidenziatore", "荧光笔", "マーカー"],
            ["paint.arrow"] = ["Arrow", "Стрелка", "Стрілка", "Pfeil", "Flecha", "Seta", "Flèche", "Freccia", "箭头", "矢印"],
            ["paint.rectangle"] = ["Frame", "Рамка", "Рамка", "Rahmen", "Marco", "Moldura", "Cadre", "Riquadro", "方框", "枠"],
            ["paint.undo"] = ["Undo", "Отменить", "Скасувати", "Rückgängig", "Deshacer", "Desfazer", "Annuler", "Annulla", "撤销", "元に戻す"],
            ["paint.redo"] = ["Redo", "Повторить", "Повторити", "Wiederholen", "Rehacer", "Refazer", "Rétablir", "Ripeti", "重做", "やり直す"],
            ["paint.clear"] = ["Clear drawings", "Убрать рисунки", "Прибрати малюнки", "Zeichnungen löschen", "Borrar dibujos", "Limpar desenhos", "Effacer les dessins", "Cancella disegni", "清除绘图", "描画を消去"],
            ["paint.width"] = ["Width", "Толщина", "Товщина", "Breite", "Grosor", "Espessura", "Épaisseur", "Spessore", "粗细", "太さ"],
            ["paint.red"] = ["Red", "Красный", "Червоний", "Rot", "Rojo", "Vermelho", "Rouge", "Rosso", "红色", "赤"],
            ["paint.orange"] = ["Orange", "Оранжевый", "Помаранчевий", "Orange", "Naranja", "Laranja", "Orange", "Arancione", "橙色", "オレンジ"],
            ["paint.yellow"] = ["Yellow", "Жёлтый", "Жовтий", "Gelb", "Amarillo", "Amarelo", "Jaune", "Giallo", "黄色", "黄"],
            ["paint.green"] = ["Green", "Зелёный", "Зелений", "Grün", "Verde", "Verde", "Vert", "Verde", "绿色", "緑"],
            ["paint.blue"] = ["Blue", "Голубой", "Блакитний", "Blau", "Azul", "Azul", "Bleu", "Blu", "蓝色", "青"],
            ["paint.purple"] = ["Purple", "Фиолетовый", "Фіолетовий", "Violett", "Morado", "Roxo", "Violet", "Viola", "紫色", "紫"],
            ["paint.white"] = ["White", "Белый", "Білий", "Weiß", "Blanco", "Branco", "Blanc", "Bianco", "白色", "白"],
            ["paint.black"] = ["Black", "Чёрный", "Чорний", "Schwarz", "Negro", "Preto", "Noir", "Nero", "黑色", "黒"],
            ["paint.ready"] = ["Draw on the image, then save as JPEG or copy.", "Добавьте пометки, затем сохраните JPEG или скопируйте снимок.", "Додайте позначки, потім збережіть JPEG або скопіюйте знімок.", "Bild markieren, dann als JPEG speichern oder kopieren.", "Dibuja y guarda como JPEG o copia la imagen.", "Desenhe e salve como JPEG ou copie a imagem.", "Annotez puis enregistrez en JPEG ou copiez.", "Annota, poi salva in JPEG o copia.", "添加标注，然后保存为 JPEG 或复制。", "描画してからJPEGで保存するかコピーします。"],
            ["paint.clipboard"] = ["Original copied. Copy again after drawing to update the clipboard.", "Исходный снимок в буфере. После рисования скопируйте результат ещё раз.", "Початковий знімок у буфері. Після малювання скопіюйте результат ще раз.", "Original kopiert. Nach dem Zeichnen erneut kopieren.", "Original copiado. Vuelve a copiar tras dibujar.", "Original copiado. Copie novamente após desenhar.", "Original copié. Copiez de nouveau après annotation.", "Originale copiato. Copia di nuovo dopo aver disegnato.", "原图已复制。绘图后请再次复制以更新剪贴板。", "元画像をコピーしました。描画後にもう一度コピーしてください。"],
            ["paint.changed"] = ["Changes ready · Copy or save to export.", "Изменения готовы · Скопируйте или сохраните результат.", "Зміни готові · Скопіюйте або збережіть результат.", "Änderungen bereit · Kopieren oder speichern.", "Cambios listos · Copia o guarda.", "Alterações prontas · Copie ou salve.", "Modifications prêtes · Copiez ou enregistrez.", "Modifiche pronte · Copia o salva.", "更改已就绪 · 请复制或保存。", "変更できました · コピーまたは保存してください。"],
            ["paint.retake"] = ["New capture", "Новый снимок", "Новий знімок", "Neue Aufnahme", "Nueva captura", "Nova captura", "Nouvelle capture", "Nuova schermata", "重新截图", "新しい撮影"],
            ["paint.keys"] = ["Ctrl+Z undo · Ctrl+Y redo · Esc close", "Ctrl+Z отмена · Ctrl+Y повтор · Esc закрыть", "Ctrl+Z скасувати · Ctrl+Y повторити · Esc закрити", "Ctrl+Z zurück · Ctrl+Y wiederholen · Esc schließen", "Ctrl+Z deshacer · Ctrl+Y rehacer · Esc cerrar", "Ctrl+Z desfazer · Ctrl+Y refazer · Esc fechar", "Ctrl+Z annuler · Ctrl+Y rétablir · Échap fermer", "Ctrl+Z annulla · Ctrl+Y ripeti · Esc chiudi", "Ctrl+Z 撤销 · Ctrl+Y 重做 · Esc 关闭", "Ctrl+Z 元に戻す · Ctrl+Y やり直し · Esc 閉じる"],
            ["paint.openAfterCopy"] = ["Open editor after copying a screenshot", "Открывать редактор после копирования скриншота", "Відкривати редактор після копіювання знімка", "Editor nach dem Kopieren öffnen", "Abrir editor tras copiar una captura", "Abrir editor após copiar captura", "Ouvrir l’éditeur après la copie", "Apri editor dopo la copia", "复制截图后打开编辑器", "コピー後に編集画面を開く"],
        };
        foreach (var (key, values) in strings)
        {
            Strings[key] = (values[0], values[1], values[2]);
            for (var i = 3; i < Languages.Count; i++) Extra[Languages[i].Code][key] = values[i];
        }
    }
}
