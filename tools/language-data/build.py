"""Build a disk-backed eight-language vocabulary. Only --fetch permits network access."""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import closing
import hashlib
import json
import math
from pathlib import Path
import sqlite3
import tempfile
import unicodedata
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
RESOURCE = ROOT / "resources/language-detection"
LANGUAGES = ("en", "ru", "uk", "fr", "es", "it", "de", "pt")
LATIN = "abcdefghijklmnopqrstuvwxyz"
CYRILLIC = "абвгдежзийклмнопрстуфхцчшщь"
ALPHABETS = {
    "en": LATIN,
    "ru": CYRILLIC + "ёъыэюя",
    "uk": CYRILLIC + "ґєіїюя",
    "fr": LATIN + "àâæçéèêëîïôœùûüÿ",
    "es": LATIN + "áéíñóúü",
    "it": LATIN + "àèéìíîòóùú",
    "de": LATIN + "äöüß",
    "pt": LATIN + "áâãàçéêíóôõúü",
}
TRANSLATE = str.maketrans({"’": "'", "ʼ": "'", "‘": "'", "‐": "-", "‑": "-"})
APPLICATION_ID = 0x54594C44  # TYLD
SCORE_SCALE = 28


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def normalize(word: str) -> str:
    return unicodedata.normalize("NFC", word.translate(TRANSLATE).lower())


def valid(word: str, language: str) -> bool:
    def letter(c):
        # Keep established loans (English café/naïve, German Café, etc.) instead of
        # falsely treating their accents as evidence that English/German are impossible.
        return c in ALPHABETS[language] if language in ("ru", "uk") else c.isalpha() and unicodedata.name(c, "").startswith("LATIN ")
    return (0 < len(word) <= 64 and letter(word[0]) and letter(word[-1])
            and all(letter(c) or (c in "'-" and i > 0 and i < len(word) - 1
                                     and letter(word[i-1]) and letter(word[i+1]))
                    for i, c in enumerate(word)))


def parse(data: bytes, language: str) -> tuple[Counter, dict]:
    words = Counter()
    rows = total = rejected = 0
    for line in data.decode("utf-8").splitlines():
        token, raw_count = line.rsplit(" ", 1)
        if not raw_count.isascii() or not raw_count.isdigit() or int(raw_count) <= 0:
            raise ValueError("Invalid source count")
        count = int(raw_count)
        rows += 1
        total += count  # Keep the denominator before filtering, including noise.
        word = normalize(token)
        if valid(word, language):
            words[word] += count
        else:
            rejected += 1
    return words, {"source_rows": rows, "retained_words": len(words),
                   "rejected_rows": rejected, "source_total_count": total}


def score(count: int, total: int) -> int:
    # Frequency relative to the source top-50k list, NOT a calibrated Zipf population estimate.
    return max(1, min(255, round((9 + math.log10(count / total)) * SCORE_SCALE)))


def checked_source(source: dict, cache: Path, fetch: bool) -> bytes:
    path = cache / (source.get("filename") or f"{source['language']}_50k.txt")
    if not path.exists():
        if not fetch:
            raise FileNotFoundError(f"Missing {path}; explicitly pass --fetch to download locked inputs")
        data = urllib.request.urlopen(source["url"], timeout=60).read()
        if len(data) != source["bytes"] or digest(data) != source["sha256"]:
            raise ValueError(f"Source checksum mismatch: {source['url']}")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    data = path.read_bytes()
    if len(data) != source["bytes"] or digest(data) != source["sha256"]:
        raise ValueError(f"Cached source checksum mismatch: {path}")
    return data


def read_supplement(path: Path) -> tuple[dict[str, int], dict]:
    raw = path.read_bytes()
    data = json.loads(raw)
    if data.get("schema") != 1 or data.get("license") != "MIT" or not isinstance(data.get("entries"), list):
        raise ValueError("Invalid IT supplement schema/license")
    masks: dict[str, int] = {}
    groups = set()
    for entry in data["entries"]:
        language, family, forms = entry.get("language"), entry.get("family"), entry.get("forms")
        if language not in ("ru", "uk") or not isinstance(family, str) or not family or (language, family) in groups:
            raise ValueError("Invalid/duplicate IT supplement family")
        groups.add((language, family))
        if not isinstance(forms, list) or not forms or any(not isinstance(w, str) for w in forms):
            raise ValueError("Invalid IT supplement forms")
        seen = set()
        for word in forms:
            if normalize(word) != word or not valid(word, language) or word in seen:
                raise ValueError(f"Invalid/duplicate IT supplement word: {word!r}")
            seen.add(word)
            masks[word] = masks.get(word, 0) | (1 << LANGUAGES.index(language))
    return masks, {"source": "tools/language-data/it-terms.json", "sha256": digest(raw), "license_text": (ROOT / "LICENSE").read_text(encoding="utf-8"), **data}


def build(output: Path, cache: Path, fetch: bool = False) -> dict:
    lock = json.loads((HERE / "sources.lock.json").read_text(encoding="utf-8"))
    if tuple(s["language"] for s in lock["files"]) != LANGUAGES:
        raise ValueError("Unexpected language order")
    entries: dict[str, bytearray] = {}
    stats = {}
    for i, source in enumerate(lock["files"]):
        words, info = parse(checked_source(source, cache, fetch), source["language"])
        if info["source_rows"] != 50_000 or len(words) < 30_000:
            raise ValueError(f"Unexpected input size for {source['language']}: {info}")
        stats[source["language"]] = info
        for word, count in words.items():
            entries.setdefault(word, bytearray(8))[i] = score(count, info["source_total_count"])
    supplement, supplement_info = read_supplement(HERE / "it-terms.json")
    for word in supplement:
        entries.setdefault(word, bytearray(8))  # Manual membership is never a fabricated corpus frequency.
    for i, language in enumerate(LANGUAGES):
        stats[language]["supplemental_words"] = sum(bool(mask & (1 << i)) for mask in supplement.values())
        stats[language]["supplemental_only_words"] = sum(bool(mask & (1 << i)) and entries[word][i] == 0 for word, mask in supplement.items())
        stats[language]["combined_words"] = stats[language]["retained_words"] + stats[language]["supplemental_only_words"]
    notice = (RESOURCE / "NOTICE.txt").read_text(encoding="utf-8")
    license_text = (RESOURCE / "LICENSE-CC-BY-SA-4.0.txt").read_text(encoding="utf-8")
    if digest(license_text.encode()) != lock["license_file"]["sha256"]:
        raise ValueError("License checksum mismatch")
    metadata = {
        "format": "typlo-language-vocabulary-v2", "languages": list(LANGUAGES),
        "source_lock": lock, "statistics": stats, "notice": notice, "license_text": license_text,
        "supplement": supplement_info,
        "normalization": "NFC + lowercase; apostrophes U+2018/U+2019/U+02BC to ASCII; U+2010/U+2011 to hyphen",
        "weights": {"type": "8 unsigned bytes, one per languages[] slot", "absent": 0,
                    "formula": "clamp(round((9 + log10(count / source_total_count)) * 28), 1, 255)",
                    "warning": "source_total_count sums only the top-50k list; not calibrated probabilities; manual membership is separate in supplement_mask"},
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix="words-", suffix=".sqlite3", dir=output.parent)
    import os
    os.close(fd)
    temporary = Path(name)
    try:
        with closing(sqlite3.connect(temporary)) as db:
            db.executescript(f"""
                PRAGMA page_size=4096;
                PRAGMA journal_mode=OFF;
                PRAGMA application_id={APPLICATION_ID};
                PRAGMA user_version=2;
                CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
                CREATE TABLE words(word TEXT PRIMARY KEY COLLATE BINARY, weights BLOB NOT NULL
                                   CHECK(length(weights)=8), supplement_mask INTEGER NOT NULL
                                   CHECK(supplement_mask IN (0,2,4,6))) WITHOUT ROWID;
            """)
            db.executemany("INSERT INTO metadata VALUES (?,?)", ((k, json.dumps(v, ensure_ascii=False, sort_keys=True))
                                                                   for k, v in sorted(metadata.items())))
            db.executemany("INSERT INTO words VALUES (?,?,?)", ((w, bytes(entries[w]), supplement.get(w, 0)) for w in sorted(entries)))
            db.commit()
            db.execute("VACUUM")
            if db.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
                raise ValueError("Database integrity check failed")
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)
    content = hashlib.sha256()
    for word in sorted(entries):
        content.update(word.encode("utf-8") + b"\0" + entries[word] + bytes([supplement.get(word, 0)]))
    return {"schema": 2, "sha256": digest(output.read_bytes()), "content_sha256": content.hexdigest(), "bytes": output.stat().st_size,
            "words": len(entries), "language_entries": sum(s["combined_words"] for s in stats.values()),
            "ambiguous_words": sum(sum(w > 0 or bool(supplement.get(word, 0) & (1 << i)) for i, w in enumerate(weights)) > 1 for word, weights in entries.items()),
            "supplement": {"sha256": supplement_info["sha256"], "unique_words": len(supplement), "families": len(supplement_info["entries"])},
            "sqlite_builder_version": sqlite3.sqlite_version, "unicode_version": unicodedata.unidata_version,
            "languages": stats}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fetch", action="store_true", help="download only missing SHA-256-locked inputs")
    parser.add_argument("--cache", type=Path, default=HERE / ".cache")
    parser.add_argument("--output", type=Path, default=RESOURCE / "words.sqlite3")
    args = parser.parse_args()
    report = build(args.output, args.cache, args.fetch)
    args.output.with_suffix(".manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
