import importlib.util
import hashlib
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

HERE = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("language_data_build", HERE / "build.py")
build = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build)


class SourceTests(unittest.TestCase):
    def test_normalization_preserves_language_distinctions(self):
        self.assertEqual(build.normalize("CAFE\u0301"), "café")
        self.assertEqual(build.normalize("П’ЯТЬ"), "п'ять")
        self.assertEqual(build.normalize("STRAẞE"), "straße")
        self.assertNotEqual(build.normalize("всё"), build.normalize("все"))
        self.assertNotEqual(build.normalize("año"), build.normalize("ano"))

    def test_alphabets_and_internal_punctuation(self):
        for lang, words in {"en": ["can't", "well-known"], "uk": ["ґрунт", "п'ять", "їжа"],
                            "ru": ["ещё", "съесть"], "fr": ["œuvre", "l'homme"], "de": ["straße"],
                            "es": ["año"], "it": ["città"], "pt": ["coração"]}.items():
            for word in words:
                self.assertTrue(build.valid(word, lang), (word, lang))
        for word in ["hello123", "http://a", "foo_bar", "'word", "word-", "a--b", "a''b", "а"*65, ""]:
            self.assertFalse(build.valid(word, "en"), word)
        self.assertTrue(build.valid("привет", "uk"))  # Script filtering alone cannot identify the language.

    def test_foreign_letters_are_removed_without_transliteration(self):
        self.assertFalse(build.valid("ещё", "uk"))
        self.assertFalse(build.valid("дякуюї", "ru"))
        self.assertFalse(build.valid("bonjour", "ru"))
        self.assertTrue(build.valid("café", "en"))
        self.assertTrue(build.valid("naïve", "en"))
        self.assertTrue(build.valid("café", "de"))
        self.assertFalse(build.valid("hello", "uk"))

    def test_normalized_duplicates_sum_counts_and_noise_stays_in_denominator(self):
        data = "CAFÉ 10\ncafé 5\nhello123 8\n".encode("utf-8")
        words, stats = build.parse(data, "fr")
        self.assertEqual(dict(words), {"café": 15})
        self.assertEqual(stats["source_total_count"], 23)
        self.assertEqual(stats["rejected_rows"], 1)

    def test_malformed_or_invalid_count_fails(self):
        for data in [b"word -1", b"word 0", b"missing", b"word nan", b"word \xff"]:
            with self.assertRaises((ValueError, UnicodeError)):
                build.parse(data, "en")

    def test_missing_source_never_downloads_implicitly(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(FileNotFoundError):
                build.checked_source({"language": "en"}, Path(folder), False)

    def test_corrupted_cached_source_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "en_50k.txt").write_bytes(b"tampered")
            with self.assertRaisesRegex(ValueError, "checksum"):
                build.checked_source({"language": "en", "bytes": 1, "sha256": "wrong"}, Path(folder), False)

    def test_supplement_validation_rejects_bad_labels_words_and_duplicates(self):
        for entries in [
            [{"language": "en", "family": "push", "forms": ["пушить"]}],
            [{"language": "ru", "family": "push", "forms": ["пуш123"]}],
            [{"language": "ru", "family": "push", "forms": ["пушить", "пушить"]}],
            [{"language": "uk", "family": "review", "forms": ["рев’ювати"]}],  # source must be canonical
            [{"language": "uk", "family": "push", "forms": []}],
            [{"language": "ru", "family": "push", "forms": ["пушить"]}]*2,
        ]:
            with self.subTest(entries=entries), tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / "invalid.json"
                path.write_text(json.dumps({"schema": 1, "license": "MIT", "entries": entries}), encoding="utf-8")
                with self.assertRaises(ValueError):
                    build.read_supplement(path)


class PackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = build.RESOURCE / "words.sqlite3"
        cls.manifest = json.loads(cls.path.with_suffix(".manifest.json").read_text(encoding="utf-8"))

    def test_manifest_hash_matches_pack(self):
        self.assertEqual(build.digest(self.path.read_bytes()), self.manifest["sha256"])

    def test_pack_is_indexed_complete_and_self_attributed(self):
        with sqlite3.connect(f"file:{self.path.as_posix()}?mode=ro", uri=True) as db:
            self.assertEqual(db.execute("PRAGMA integrity_check").fetchone()[0], "ok")
            self.assertEqual(db.execute("PRAGMA user_version").fetchone()[0], 3)
            self.assertEqual(db.execute("SELECT count(*) FROM words").fetchone()[0], self.manifest["words"])
            meta = {k: json.loads(v) for k, v in db.execute("SELECT key,value FROM metadata")}
            self.assertEqual(meta["languages"], list(build.LANGUAGES))
            self.assertIn("Hermit Dave", meta["notice"])
            self.assertIn("OpenSubtitles", meta["notice"])
            self.assertIn("Section 3", meta["license_text"])
            self.assertEqual(meta["supplement"]["license"], "MIT")
            self.assertIn("bitcodepro", meta["supplement"]["license_text"])
            plan = str(db.execute("EXPLAIN QUERY PLAN SELECT weights FROM words WHERE word=?", ("hello",)).fetchall())
            self.assertIn("SEARCH words USING PRIMARY KEY", plan)
            digest = hashlib.sha256()
            corpus_digest = hashlib.sha256()
            counts = [0]*8
            for word, weights, mask in db.execute("SELECT word,weights,supplement_mask FROM words ORDER BY word COLLATE BINARY"):
                self.assertEqual(len(weights), 8)
                self.assertIn(mask, range(8))
                self.assertTrue(any(weights) or mask)
                for i, weight in enumerate(weights):
                    if weight or (mask & (1 << i)):
                        self.assertTrue(build.valid(word, build.LANGUAGES[i]), (word, i))
                        counts[i] += 1
                digest.update(word.encode("utf-8") + b"\0" + weights + bytes([mask]))
                if any(weights):
                    corpus_digest.update(word.encode("utf-8") + b"\0" + weights)
            self.assertEqual(digest.hexdigest(), self.manifest["content_sha256"])
            # Adding manual IT vocabulary must leave every original corpus weight unchanged.
            self.assertEqual(corpus_digest.hexdigest(), "2e508eb9ec0b0c9b0387a5b571ebd11af3776ca4a7a5c0e5947c814144e9bbb7")
            self.assertEqual(counts, [self.manifest["languages"][l]["combined_words"] for l in build.LANGUAGES])

    def test_common_and_ambiguous_words(self):
        with sqlite3.connect(f"file:{self.path.as_posix()}?mode=ro", uri=True) as db:
            for i, word in enumerate(["hello", "привет", "дякую", "bonjour", "gracias", "buongiorno", "danke", "obrigado"]):
                weights = db.execute("SELECT weights FROM words WHERE word=?", (word,)).fetchone()[0]
                self.assertGreater(weights[i], 0)
            for word in ["chat", "так", "no"]:
                weights = db.execute("SELECT weights FROM words WHERE word=?", (word,)).fetchone()[0]
                self.assertGreater(sum(w > 0 for w in weights), 1)
            self.assertIsNone(db.execute("SELECT weights FROM words WHERE word='xqzvnotaword987'").fetchone())

    def test_every_curated_it_form_is_stored_with_exact_provenance(self):
        masks, info = build.read_supplement(HERE / "it-terms.json")
        self.assertEqual(info["sha256"], self.manifest["supplement"]["sha256"])
        with sqlite3.connect(f"file:{self.path.as_posix()}?mode=ro", uri=True) as db:
            actual = dict(db.execute("SELECT word,supplement_mask FROM words WHERE supplement_mask<>0"))
            self.assertEqual(actual, masks)
            for language, words in {"en": ["sql", "nosql", "mysql", "postgresql", "sqlite"], "ru": ["ревьюить", "ревьюваешь", "коммитишь", "закоммитил", "пушить", "запушили", "смерджили"],
                                    "uk": ["рев’ювити", "ревʼювати", "рев'юїти", "комітити", "комітиш", "закомітила", "пушити", "запушили", "змерджили"]}.items():
                for word in words:
                    self.assertTrue(actual[build.normalize(word)] & (1 << build.LANGUAGES.index(language)), (language, word))
            for word in ["пайплайн", "деплой", "пушили"]:
                self.assertEqual(actual[word], 6)
            # No runtime morphological/prefix guessing, nor marks for Latin or unrelated languages.
            for word in ["суперпушить", "абракадабрить", "notarealcommit"]:
                self.assertIsNone(db.execute("SELECT word FROM words WHERE word=?", (word,)).fetchone())


if __name__ == "__main__":
    unittest.main()
