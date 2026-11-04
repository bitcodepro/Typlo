"""Verify the exact vendored spelling files; no downloads or newline rewriting."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "resources" / "spelling"

def verify():
    manifest = json.loads((DATA / "manifest.json").read_text(encoding="utf-8"))
    expected = {"en_US.aff", "en_US.dic", "ru_RU.aff", "ru_RU.dic", "uk_UA.aff", "uk_UA.dic",
                "LICENSE-en_US.txt", "LICENSE-ru_RU.txt", "LICENSE-uk_UA.txt", "LICENSE-WeCantSpell.txt",
                "MPL-1.1.txt", "WeCantSpell.Hunspell-7.0.1-source.tar.gz", "NOTICE.txt"}
    assert set(manifest["files"]) == expected
    for name, entry in manifest["files"].items():
        raw = (DATA / name).read_bytes()
        assert len(raw) == entry["bytes"], name
        assert hashlib.sha256(raw).hexdigest() == entry["sha256"], name
    print(f"Verified {len(expected)} spelling data, source and license files")

if __name__ == "__main__":
    verify()
