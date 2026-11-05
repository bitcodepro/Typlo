# Offline spelling

Select text and press **Ctrl+Shift+L** to correct spelling directly, without a preview window or focus activation. The command uses each unknown word's top suggestion, ranked by edit distance (including adjacent transpositions) and bundled vocabulary evidence. Words without suggestions stay unchanged. The complete selection is replaced once and supports the normal Typlo Undo shortcut. This is independent of wrong-layout correction after Space.

**Ctrl+Shift+P** opens the optional review window. Choose suggestions/language yourself, use **My corrections** to add/edit/delete exact word pairs such as `превет → привет`, or open the personal-word dictionary. Both shortcuts can be changed or disabled in Settings. Saved pairs take priority over dictionary suggestions, including for otherwise valid words. Exclusions take priority over pairs. A pair runs once per original token, without recursive replacements; normal casing is preserved. Up to 1,000 pairs of two distinct words (2–64 letters with internal apostrophes/hyphens) are supported. Pairs are stored with settings and included in settings export/full backups; they do not change the bundled dictionaries.

Direct Apply requires foreground notifications, a foreground-transition counter and a nonzero input-field identity, both at capture and at application time. When those guarantees are unavailable (currently macOS/Linux), Apply is disabled: review normally, use **Copy corrected text**, and paste into the intended field yourself. The resident also rejects direct Apply requests in that mode before activation, copying or writing. Identical text in another field/window is never treated as a substitute for missing target identity.

Saving Settings preserves the latest correction pairs even if that window was opened before they changed. Settings export also uses the current pairs; explicit settings imports/full restores replace them. Pair edits leave shortcuts paused until Settings closes. Casing ignores separators: `HELO-WORLD` becomes `HELLO-WORLD`, including words with straight or Unicode apostrophes.

The silent command also skips unsupported targets. While its worker is active it checks the original window/field, input/focus generation and clipboard sequence; a newer command or manual text action cancels it before writing. It never reactivates a source window. After a write is sent it only finishes verification/Undo bookkeeping, without retrying a write. Shutdown cancels the helper; updates wait for it to exit before swapping files. Tools includes text-free `quick-spelling` outcome/timing entries.

English, Russian and Ukrainian Hunspell `.aff`/`.dic` packs are bundled in `spelling/`; extract the entire distribution. Auto mode accepts words valid in any matching language, uses paragraph evidence for Cyrillic suggestions, and offers both RU/UK candidates when uncertain. This is spelling, not contextual grammar correction. Names and slang may need personal entries. Personal entries, exclusions and the bundled IT supplement are respected.

Ordinary surrounding prose punctuation does not hide Title Case typos: `(Speling)`, `«Превет»` and `“Привітт”` are checked with their original offsets and punctuation preserved. Quoted URLs, paths, mixed-case identifiers and empty-argument calls such as `Speling()` stay protected. Internal apostrophes and hyphens in words are retained.

The command reads only the selection, never a whole document automatically. The preview is bounded to 20,000 characters, 3,000 words and 300 reported unknown words. Source selection is copied and compared again before Apply; typing, focus/field changes during the operation and external clipboard changes abort the write. Apply returns to the captured source window. Plain-text replacement preserves whitespace but can remove rich formatting. The normal Typlo Undo shortcut can undo a confirmed replacement until further input.

## Process and resource lifetime

The resident handles selection capture and guarded application. Silent correction or the optional preview starts the same executable with `--spelling-worker`, before Avalonia/platform initialization. Text and results use redirected standard streams, not command-line arguments, temporary files or network services. Dictionaries are loaded lazily only in this worker, which exits after one request. Ranking also uses the existing disk-based vocabulary with its bounded SQLite cache. No spelling dictionaries are preloaded by the resident or UI. Closing the preview cancels/kills the worker and waits for its exit. A 30-second deadline bounds each check. Each language recheck starts a fresh worker.

Worker metrics include working set, peak working set, managed memory and elapsed checking time. Reproduce headless AOT/protocol/memory checks without input hooks or GUI:

```
dotnet publish src/Typlo.App -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts/spelling-aot
dotnet run --project tools/SpellingProbe -c Release -- artifacts/spelling-aot/Typlo.exe
python tools/spelling-data/verify.py
```

The probe measures a headless client before and after five dictionary checks, then checks personal-pair/exclusion roundtrips and worker cancellation. It does not measure the installed resident or a visual editor. GC in the probe is for measurement only; the product does not force GC after spelling checks. Editor integration still needs interactive verification where the clipboard/selection fallback is used.

### Quick-command Windows x64 Native AOT sample (2026-11-05)

With suggestion ranking enabled, short checks took 389 ms (EN), 874 ms (RU), 695 ms (UK) and 944–976 ms (mixed), including process startup. Worker peak working sets ranged from 26.7 to 102.4 MiB; each worker exited after its request. The headless client's managed heap after collection was 282 KiB (133 KiB before), with private bytes growing from 6.9 to 10.0 MiB. Custom pairs, exclusion precedence and active-worker cancellation also passed. These measurements do not establish resident idle memory or editor responsiveness. Raw results: [spelling-quick-benchmark-win-x64.json](spelling-quick-benchmark-win-x64.json).

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
