# Offline spelling

Select text, press **Ctrl+Shift+L**, review the suggestions and apply. The shortcut can be changed or disabled in Settings. It is independent of wrong-layout correction after Space. No word is changed until a suggestion is selected and Apply is pressed. Language can be selected manually.

English, Russian and Ukrainian Hunspell `.aff`/`.dic` packs are bundled in `spelling/`; extract the entire distribution. Auto mode accepts words valid in any matching language, uses paragraph evidence for Cyrillic suggestions, and offers both RU/UK candidates when uncertain. This is spelling, not contextual grammar correction. Names and slang may need personal entries. Personal entries, exclusions and the bundled IT supplement are respected.

The command reads only the selection, never a whole document automatically. The preview is bounded to 20,000 characters, 3,000 words and 300 reported unknown words. Source selection is copied and compared again before Apply; typing, focus/field changes during the operation and external clipboard changes abort the write. Apply returns to the captured source window. Plain-text replacement preserves whitespace but can remove rich formatting. The normal Typlo Undo shortcut can undo a confirmed replacement until further input.

## Process and resource lifetime

The resident handles selection capture and guarded application only. The preview starts the same executable with `--spelling-worker`, before Avalonia/platform initialization. Text and results use redirected standard streams, not command-line arguments, temporary files or network services. Dictionaries are loaded lazily only in this worker, which exits after one request. No dictionaries are preloaded by the resident or UI. Closing the preview cancels/kills the worker and waits for its exit. A 30-second deadline bounds each check. Each language recheck starts a fresh worker.

Worker metrics include working set, peak working set, managed memory and elapsed checking time. Reproduce headless AOT/protocol/memory checks without input hooks or GUI:

```
dotnet publish src/Typlo.App -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts/spelling-aot
dotnet run --project tools/SpellingProbe -c Release -- artifacts/spelling-aot/Typlo.exe
python tools/spelling-data/verify.py
```

The probe measures a headless client before and after five worker checks, not the installed resident or a visual editor. GC in the probe is for measurement only; the product does not force GC after spelling checks. Editor integration still needs interactive verification where the clipboard/selection fallback is used.

### Local Windows x64 Native AOT sample (2026-11-04)

Each request starts a fresh process; the OS file cache was warm. These are short synthetic texts, not a latency guarantee for long selections.

| Request | End-to-end | Worker peak working set |
|---|---:|---:|
| English `speling` | 193 ms | 24.5 MiB |
| Russian `превет` | 649 ms | 92.5 MiB |
| Ukrainian `привітт` | 668 ms | 97.4 MiB |
| Mixed EN/RU/UK | 889–897 ms | 99.1 MiB |

All workers exited before the next request. Active-process cancellation and already-canceled requests passed too. The headless client's managed heap after collection increased from about 123 KiB to 273 KiB; private bytes increased from 6.6 MiB to 9.1 MiB after initialization of process/IPC machinery. This measurement does not include Avalonia preview-window memory or establish the installed resident's idle working set. Raw results: [spelling-benchmark-win-x64.json](spelling-benchmark-win-x64.json).

## Third-party licenses and provenance

Typlo's own code remains MIT. The following components retain their own licenses; they are not relicensed as MIT:

* **WeCantSpell.Hunspell 7.0.1**: https://github.com/aarondandy/WeCantSpell.Hunspell/tree/7.0.1 . We elect **MPL 1.1** from its MPL 1.1 / GPL / LGPL license alternatives. The original license notice, full MPL text and the exact version's upstream source archive are shipped in `spelling/`. Our integration is separate from the library's source; the library is unmodified. Recipients can obtain and modify the covered source under MPL 1.1. See `LICENSE-WeCantSpell.txt`, `MPL-1.1.txt`, `WeCantSpell.Hunspell-7.0.1-source.tar.gz`.
* **Russian ru_RU**: Alexander I. Lebedev and contributors; permissive BSD-like terms with a modification-marking requirement and no endorsement by the author's name. See `LICENSE-ru_RU.txt`.
* **Ukrainian uk_UA Hunspell distribution**: Andriy Rysin and contributors; **MPL 1.1**, per `LICENSE-uk_UA.txt`. This is the separately licensed Hunspell distribution, not a redistribution of the main VESUM dataset under its NonCommercial license. The unmodified editable `.aff` and `.dic` files are shipped together with their license and the full MPL text. Upstream confirmation: https://github.com/brown-uk/dict_uk/blob/master/distr/hunspell/README.md .
* **English en_US**: SCOWL/Ispell-derived dictionary, with the permissive and public-domain source terms reproduced in full in `LICENSE-en_US.txt`. Preserve every notice; do not assume all other files in LibreOffice's `en/` directory have the same terms.

All six dictionary files and their three README/license notices are copied **unmodified** from LibreOffice/dictionaries commit `4f1df64b2e8e14751aa7c2205d170ca016071315`, using only `en/en_US.*`, `en/README_en_US.txt`, `ru_RU/ru_RU.*`, `ru_RU/README_ru_RU.txt`, `uk_UA/uk_UA.*`, `uk_UA/README_uk_UA.txt`. They are kept separate from Typlo's personal words and IT supplement. The manifest records exact byte sizes and SHA-256 hashes; `.gitattributes` prevents newline conversion of upstream files. When updating, recheck the exact licenses, update pinned inputs/hashes, and mark any modified dictionaries.
