# Language vocabulary preparation

This branch prepares data and a standalone .NET reader for a future language detector.
It does not change Typlo's conversion, hotkeys, settings, packaging or application startup.
The data and probe are deliberately outside the application/solution build. Integration
with automatic language detection remains a separate step. CI runs the Python data
checks and independently verifies the pack with the .NET reader on all three OSes.

## Data

`resources/language-detection/words.sqlite3` contains **292,798 unique words** and
391,148 word/language associations for English, Russian, Ukrainian, French, Spanish,
Italian, German and Portuguese. The language order is `en, ru, uk, fr, es, it, de, pt`.
The database occupies 7,458,816 bytes (7.11 MiB), including provenance and the full licenses.
Per-language counts and hashes are in `words.manifest.json`.

The input is the top 50,000 words per language from
[FrequencyWords / OPUS OpenSubtitles 2018](https://github.com/hermitdave/FrequencyWords/tree/525f9b560de45753a5ea01069454e72e9aa541c6/content/2018).
All eight downloads are pinned to that commit and SHA-256-locked in `sources.lock.json`.
The database is an adaptation under **CC BY-SA 4.0**, with attribution to Hermit Dave,
OPUS and OpenSubtitles. Redistribute its notice/license with it. The MIT source-code
license does not replace the data license. Attribution and the full license are also
embedded inside the database so they travel with the data.

The build normalizes NFC, lowercase and common apostrophe/hyphen variants. It preserves
accents, `ё/е`, `і/и`, `ß`, and established Latin-script loans such as English `café`.
It removes numbers, invalid token punctuation, incompatible scripts and Cyrillic
letters outside the respective RU/UK orthography. Normalized duplicate counts are added.

Format v2 (`PRAGMA user_version=2`) adds manual membership separately from frequencies.
Each `words` row has an exact UTF-8 word key, an eight-byte corpus frequency vector,
and `supplement_mask`. A zero frequency means absent from the corpus, not necessarily
absent from the vocabulary. Each nonzero frequency byte is
`clamp(round((9 + log10(count / source_total_count)) * 28), 1, 255)`.
The denominator sums the original top-50k source list before filtering. These are
quantized relative corpus weights, **not calibrated language probabilities or
full-corpus Zipf frequencies**. Do not compare raw subtitle counts across languages.
The `WITHOUT ROWID` primary-key B-tree searches exact words; no probabilistic hash
or Bloom filter can falsely classify an unknown word as present.

## Russian and Ukrainian IT vocabulary

[it-terms.json](it-terms.json) is an original, curated seed list under the repository's
MIT license: 1,137 distinct forms across 122 named groups, 659 RU and 634 UK associations
(156 forms shared). It includes infinitives, common personal/past forms, prefixed verbs,
nouns and explicit colloquial spelling variants, for example:

- RU: `ревьюить`, `ревьюваешь`, `коммитишь`, `закоммитили`, `пушить`, `запушил`, `смерджили`.
- UK: `рев'ювити`, `рев'ювати`, `рев'юїти`, `комітити`, `комітиш`, `закомітила`, `пушити`, `змерджили`.
- Both: `пайплайн`, `деплой`, `пушили`; shared forms keep both language labels.

The JSON is a finite, editable list of explicit forms. The builder and reader never
guess arbitrary suffixes/prefixes or load a runtime morphology library. Apostrophe
normalization accepts `рев’ювити`/`ревʼювити`/`рев'ювити` with the same indexed lookup.
These are colloquial terms, not a declaration of normative spelling.

`supplement_mask` uses language-order bits (RU=2, UK=4, both=6). Manual-only words have
zero corpus weights; no subtitle counts or probability scores are invented. Membership
is `(weights[i] > 0) OR (supplement_mask & (1 << i))`. The .NET reader returns both kinds
of evidence separately. Tests fingerprint every original corpus word/vector to prove
the IT addition never changes frequency evidence. The combined database retains
CC BY-SA 4.0; metadata embeds the supplemental JSON, its hash and full MIT attribution.
An eventual detector must assign/calibrate a policy for manual membership separately
from corpus frequency; simply treating a zero corpus weight as absent would lose it.

## Memory and lookup prototype

`tools/LanguageDataProbe` uses the existing Microsoft.Data.Sqlite dependency:

- Lazy opening: constructing a reader does not open/read the pack.
- One read-only connection; private SQLite page cache target 128 KiB, no pooling,
  no memory mapping, no dictionary preload, no unbounded word/result cache.
- One prepared indexed query returns all eight weights and the IT membership mask;
  no eight separate searches or extra in-memory slang dictionary.
- Immutable URI avoids per-query filesystem change checks. Treat an open pack as
  immutable; close readers before replacing/updating the file. Never modify it in place.
- Dispose closes the statement and connection. The 128 KiB cache setting is a cache
  target, not a claim that the entire .NET/SQLite process uses 128 KiB.

The probe is serialized for safe reuse. A production caller should scope one lazy
reader to language detection, release it after inactivity, and keep recognition off
the input-hook/UI thread. No such application integration is enabled in this branch.

## Reproduce

Python's standard library is sufficient for building/testing; no Python is needed
in the eventual running application.

```powershell
python tools/language-data/build.py --fetch
python -B -m unittest discover -s tools/language-data/tests -v
dotnet run --project tools/LanguageDataProbe -c Release -- verify resources/language-detection/words.sqlite3
dotnet run --project tools/LanguageDataProbe -c Release -- lookup resources/language-detection/words.sqlite3 hello привет дякую bonjour gracias buongiorno danke obrigado chat так
dotnet run --project tools/LanguageDataProbe -c Release -- lookup resources/language-detection/words.sqlite3 ревьюваешь закоммитили запушили комітити закомітила пайплайн
dotnet run --project tools/LanguageDataProbe -c Release -- bench resources/language-detection/words.sqlite3
dotnet publish tools/LanguageDataProbe -c Release -r win-x64 --self-contained true -p:PublishAot=true -o dist/language-data-probe
```

Without `--fetch`, the builder is offline and refuses missing or corrupted cached
sources. It writes an atomic replacement after closing SQLite and checking integrity.
The offline builder can use substantially more RAM than the runtime reader; it is
a preparation tool, not a resident service. Repeated builds with Python Unicode 15.0
and SQLite 3.53.1 produced byte-identical packs. Other SQLite/Unicode versions may
produce different file bytes; the manifest separately records a canonical ordered
word/vector/mask checksum, which the .NET verifier checks independently.

The benchmark preloads a small workload of evenly spaced keys plus unknown words,
warms SQLite/native code and the filesystem cache, then times 100,000 mixed lookups.
It reports first reader open, median/p95/p99, retained managed allocation, first-open
private commit delta and the stress-run private commit delta. These are measurements
of an isolated prototype on the current machine, not total Typlo idle-memory figures
or cold-disk/other-OS performance guarantees. Keep the workload arrays alive across
both memory snapshots to avoid counting their collection as dictionary savings.

The recorded [Windows Native AOT run](benchmark-win-x64.txt) measured a 0.017 ms
median and 0.045 ms p95 lookup, 1.04 ms first reader open/lookup, and about 16 KiB
retained managed data. The 100k-query stress phase increased private commit by
about 5.7 MiB; that includes allocator/GC capacity and native memory. The signed
first-open process delta was noisy/negative and is not a memory-saving claim.

## Limits and next integration step

There are 52,747 words shared by multiple language lists. `chat`, `no` and `так`
must remain ambiguous. Subtitle corpora contain names, loanwords, spelling mistakes
and foreign speech; script filtering is not semantic validation. The Ukrainian source
is much smaller (4.7 million top-list tokens vs English's 725 million) and includes
Russian contamination; even a shared-alphabet word such as `привет` can survive filtering.
This is not an authoritative spelling dictionary, and slang/inflections/compound words
can be missing. Corpus vocabulary age and subtitle-domain bias also limit coverage.

The future detector should score text/candidate layout conversions using these weights,
the enabled OS layouts, script evidence and the current layout hint. It must abstain
for short/shared words, weak margins, unknown input and mixed-language text. Do not
automatically change a language merely because its dictionary has a match. Reliable
accuracy still needs separate phrase/mixed-text/wrong-layout evaluation; the current
verification cases test data/lookup correctness, not classifier accuracy.
