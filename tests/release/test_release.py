import io
import json
from pathlib import Path
import stat
import struct
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import plistlib

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import release

COMMIT = "a" * 40
VERSION = "0.1.0"


def executable(rid):
    header = bytearray(256)
    if rid == "win-x64":
        header[:2] = b"MZ"
        struct.pack_into("<I", header, 60, 128)
        header[128:134] = b"PE\0\0\x64\x86"
    elif rid.startswith("linux-"):
        header[:6] = b"\x7fELF\x02\x01"
        struct.pack_into("<H", header, 18, 183 if rid == "linux-arm64" else 62)
    else:
        header[:4] = b"\xcf\xfa\xed\xfe"
        struct.pack_into("<I", header, 4, 0x100000C if rid == "osx-arm64" else 0x1000007)
    return bytes(header)


def make_archive(directory, rid, change=None, link=False):
    files = {"README.md": b"Typlo", "LICENSE": b"MIT License\nCopyright (c) 2026 bitcodepro", release.NOTICES: b"Components in this build\n  Avalonia 12.1.3\n  SkiaSharp 3.119.4\nLicense for: Avalonia\nMIT", "build-info.json": json.dumps({"version": VERSION, "rid": rid, "commit": COMMIT}).encode(), release.executable_path(rid): executable(rid)}
    if rid.startswith("osx-"):
        files["Typlo.app/Contents/Info.plist"] = plistlib.dumps({"CFBundleShortVersionString": VERSION, "CFBundleExecutable": "Typlo"})
    if rid.startswith("linux-"):
        files.update({"gnome-extension/" + name: (release.ROOT / "integrations/gnome" / name).read_bytes()
                      for name in release.GNOME_FILES})
    data_prefix = "Typlo.app/Contents/MacOS/language-data/" if rid.startswith("osx-") else "language-data/"
    files.update({data_prefix + name: (release.ROOT / "resources/language-detection" / name).read_bytes()
                  for name in ("words.sqlite3", "words.manifest.json", "NOTICE.txt", "LICENSE-CC-BY-SA-4.0.txt")})
    if change:
        change(files)
    archive = directory / release.archive_name(VERSION, rid)
    if release.extension(rid) == "zip":
        with zipfile.ZipFile(archive, "w") as bundle:
            for name, data in files.items():
                entry = zipfile.ZipInfo(name)
                entry.create_system = 3
                entry.external_attr = (stat.S_IFREG | 0o755) << 16
                if link and name == release.executable_path(rid):
                    entry.external_attr = (stat.S_IFLNK | 0o777) << 16
                bundle.writestr(entry, data)
    else:
        with tarfile.open(archive, "w:gz") as bundle:
            for name, data in files.items():
                entry = tarfile.TarInfo(name)
                entry.size = len(data)
                entry.mode = 0o755
                if link and name == release.executable_path(rid):
                    entry.type = tarfile.SYMTYPE
                    entry.linkname = "/etc/passwd"
                bundle.addfile(entry, io.BytesIO(data))
    archive.with_name(archive.name + ".sha256").write_text(f"{release.digest(archive)}  {archive.name}\n", encoding="ascii")
    return archive


class VersionTests(unittest.TestCase):
    def test_first_release_and_numeric_order(self):
        self.assertEqual(release.select_version([])["version"], "0.1.0")
        self.assertEqual(release.select_version(["v0.9.0", "v0.10.0", "v9.0.0-rc.1"])["version"], "0.10.1")

    def test_only_subject_controls_bump_or_skip(self):
        tags = ["v1.2.3"]
        for message, expected in [("Fix\n[major] [skip release]", "1.2.4"), ("[minor] New feature", "1.3.0"), ("[major] New API", "2.0.0")]:
            with self.subTest(message=message):
                self.assertEqual(release.select_version(tags, message)["version"], expected)
        self.assertEqual(release.select_version(tags, "[skip release] Docs"), {"skip": "true"})

    def test_explicit_version_overrides_skip_and_marks_prerelease(self):
        value = release.select_version(["v0.1.0"], "[skip release]", explicit="0.2.0-rc.1")
        self.assertEqual(value["version"], "0.2.0-rc.1")
        self.assertEqual(value["prerelease"], "true")

    def test_rejects_existing_or_regressing_stable_versions(self):
        for version in ("1.2.3", "1.2.2"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                release.select_version(["v1.2.3"], explicit=version)

    def test_rejects_unsafe_or_unrepresentable_versions(self):
        for version in ("v1.2.3", "01.2.3", "1.2.3+build", "1.2.3-01", "../release", "1.2.3\n4.5.6", "65535.0.0"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                release.version_tuple(version)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def test_all_five_platform_packages_and_checksums(self):
        for rid in release.RIDS:
            make_archive(self.directory, rid)
        self.assertEqual(len(release.verify_packages(self.directory, VERSION, COMMIT)), 10)

    def test_windows_native_aot_libraries_are_required_and_architecture_checked(self):
        def aot(files):
            manifest = json.loads(files["build-info.json"])
            manifest["deployment"] = "native-aot"
            files["build-info.json"] = json.dumps(manifest).encode()
            files.update({name: executable("win-x64") for name in release.WINDOWS_NATIVE_FILES})
        archive = make_archive(self.directory, "win-x64", aot)
        release.verify_archive(archive, VERSION, "win-x64", COMMIT)
        for index, mutation in enumerate((lambda files: files.pop("e_sqlite3.dll"),
                                          lambda files: files.update({"libSkiaSharp.dll": executable("linux-x64")}),
                                          lambda files: files.update({"other.dll": executable("win-x64")}))):
            target = self.directory / str(index)
            target.mkdir()
            def change(files):
                aot(files)
                mutation(files)
            archive = make_archive(target, "win-x64", change)
            with self.assertRaises(ValueError):
                release.verify_archive(archive, VERSION, "win-x64", COMMIT)

    def test_rejects_modified_archive(self):
        archive = make_archive(self.directory, "win-x64")
        with archive.open("ab") as stream:
            stream.write(b"tampered")
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            release.verify_archive(archive, VERSION, "win-x64", COMMIT)

    def test_linux_package_requires_the_complete_gnome_bridge(self):
        archive = make_archive(self.directory, "linux-x64", lambda files: files.pop("gnome-extension/bridge.js"))
        with self.assertRaisesRegex(ValueError, "Missing package entries"):
            release.verify_archive(archive, VERSION, "linux-x64", COMMIT)

    def test_other_packages_do_not_accept_linux_bridge_files(self):
        archive = make_archive(self.directory, "win-x64", lambda files: files.update({"gnome-extension/extension.js": b"unexpected"}))
        with self.assertRaisesRegex(ValueError, "Unexpected top-level"):
            release.verify_archive(archive, VERSION, "win-x64", COMMIT)

    def test_rejects_architecture_mismatch(self):
        archive = make_archive(self.directory, "linux-arm64", lambda files: files.update(Typlo=executable("linux-x64")))
        with self.assertRaisesRegex(ValueError, "architecture"):
            release.verify_archive(archive, VERSION, "linux-arm64", COMMIT)

    def test_rejects_wrong_source_even_with_correct_checksum(self):
        archive = make_archive(self.directory, "win-x64")
        with self.assertRaisesRegex(ValueError, "source/version"):
            release.verify_archive(archive, VERSION, "win-x64", "b" * 40)

    def test_vocabulary_and_its_attribution_must_ship_unchanged(self):
        for index, name in enumerate(("words.sqlite3", "words.manifest.json", "NOTICE.txt", "LICENSE-CC-BY-SA-4.0.txt")):
            folder = self.directory / str(index)
            folder.mkdir()
            archive = make_archive(folder, "win-x64", lambda files: files.pop("language-data/" + name))
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "Missing package entries"):
                release.verify_archive(archive, VERSION, "win-x64", COMMIT)
        folder = self.directory / "changed"
        folder.mkdir()
        archive = make_archive(folder, "win-x64", lambda files: files.update({"language-data/words.sqlite3": b"wrong data"}))
        with self.assertRaisesRegex(ValueError, "Vocabulary data or attribution"):
            release.verify_archive(archive, VERSION, "win-x64", COMMIT)

    def test_rejects_missing_executable_and_data_leaks(self):
        changes = [lambda files: files.pop("Typlo.exe"), lambda files: files.update({"data/history.db": b"private"}), lambda files: files.update({"../escape.txt": b"bad"}), lambda files: files.update({".env": b"bad"}), lambda files: files.update({"libHarfBuzzSharp.pdb": b"symbols"})]
        for index, change in enumerate(changes):
            subdirectory = self.directory / str(index)
            subdirectory.mkdir()
            archive = make_archive(subdirectory, "win-x64", change)
            with self.subTest(index=index), self.assertRaises(ValueError):
                release.verify_archive(archive, VERSION, "win-x64", COMMIT)

    def test_rejects_extra_folders_and_incomplete_notices(self):
        with tempfile.TemporaryDirectory() as temporary:
            for rid in ("win-x64", "linux-x64"):
                with self.subTest(rid=rid):
                    archive = make_archive(Path(temporary), rid, lambda files: files.update({"licenses/avalonia/LICENSE": b"MIT"}))
                    with self.assertRaisesRegex(ValueError, "Unexpected top-level"):
                        release.verify_archive(archive, VERSION, rid, COMMIT)
                    archive.unlink()
                    archive.with_name(archive.name + ".sha256").unlink()
                    archive = make_archive(Path(temporary), rid, lambda files: files.update({release.NOTICES: b"Dependencies"}))
                    with self.assertRaisesRegex(ValueError, "notices are incomplete"):
                        release.verify_archive(archive, VERSION, rid, COMMIT)
                    archive.unlink()
                    archive.with_name(archive.name + ".sha256").unlink()

    def test_shipped_packages_skip_build_only_and_other_platforms(self):
        assets = {"targets": {"net10.0/win-x64": {
            "Avalonia/12.1.3": {"type": "package", "runtime": {"lib/net8.0/Avalonia.dll": {}}},
            "SkiaSharp.NativeAssets.Win32/3.119.4": {"type": "package", "native": {"runtimes/win-x64/native/libSkiaSharp.dll": {}}},
            "SkiaSharp.NativeAssets.Linux/3.119.4": {"type": "package", "runtime": {"lib/net8.0/_._": {}}},
            "Microsoft.NET.ILLink.Tasks/10.0.13": {"type": "package", "build": {"build/x.props": {}}},
            "Typlo.Core/1.0.0": {"type": "project", "runtime": {"bin/placeholder/Typlo.Core.dll": {}}},
        }}}
        self.assertEqual(release.shipped_packages(assets, "win-x64"), ["Avalonia/12.1.3", "SkiaSharp.NativeAssets.Win32/3.119.4"])

    def test_rejects_zip_and_tar_links(self):
        for rid in ("osx-arm64", "linux-x64"):
            archive = make_archive(self.directory, rid, link=True)
            with self.subTest(rid=rid), self.assertRaises(ValueError):
                release.verify_archive(archive, VERSION, rid, COMMIT)

    def test_rejects_missing_platform_or_extra_files(self):
        make_archive(self.directory, "win-x64")
        with self.assertRaises(ValueError):
            release.verify_packages(self.directory, VERSION, COMMIT)
        (self.directory / "unexpected.txt").write_text("unexpected")
        with self.assertRaises(ValueError):
            release.verify_packages(self.directory, VERSION, COMMIT, ("win-x64",))


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.env = {"GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": "bitcodepro/Typlo", "GITHUB_REF": "refs/heads/main", "GITHUB_EVENT_NAME": "push", "GITHUB_WORKFLOW_REF": "bitcodepro/Typlo/.github/workflows/ci.yml@refs/heads/main", "GITHUB_SHA": COMMIT}

    def test_unauthorized_context_never_reads_or_writes_github(self):
        for key, value in [("GITHUB_REF", "refs/pull/1/merge"), ("GITHUB_REPOSITORY", "someone/Typlo"), ("GITHUB_EVENT_NAME", "pull_request"), ("GITHUB_WORKFLOW_REF", "bitcodepro/Typlo/.github/workflows/other.yml@refs/heads/main"), ("GITHUB_SHA", "main"), ("GITHUB_ACTIONS", "false")]:
            with self.subTest(key=key), patch.object(release, "gh_api") as api:
                with self.assertRaises(ValueError):
                    release.publish(Path("unused"), VERSION, dict(self.env, **{key: value}))
                api.assert_not_called()

    def test_packages_and_current_main_are_checked_before_tag_creation(self):
        with patch.object(release, "verify_packages", return_value=[]), patch.object(release, "gh_api", return_value={"object": {"sha": "b" * 40}}) as api:
            with self.assertRaisesRegex(ValueError, "main moved"):
                release.publish(Path("unused"), VERSION, self.env)
            self.assertEqual(api.call_args_list[0].args, ("git/ref/heads/main",))
            self.assertEqual(api.call_count, 1)

    def test_existing_tag_or_draft_is_not_overwritten(self):
        for tags, drafts in [([{"ref": "refs/tags/v0.1.0"}], []), ([], [{"tag_name": "v0.1.0"}])]:
            with self.subTest(tags=tags), patch.object(release, "verify_packages", return_value=[]), patch.object(release, "gh_api", side_effect=[{"object": {"sha": COMMIT}}, tags, drafts]) as api:
                with self.assertRaises(ValueError):
                    release.publish(Path("unused"), VERSION, self.env)
                self.assertFalse(any(call.args[1:] for call in api.call_args_list))

    def test_incomplete_upload_keeps_draft(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            file = directory / "asset.zip"
            file.write_bytes(b"asset")
            responses = [{"object": {"sha": COMMIT}}, [], [], {}, [{"tag_name": "v0.1.0", "assets": []}]]
            with patch.object(release, "verify_packages", return_value=[file]), patch.object(release, "gh_api", side_effect=responses), patch.object(release, "run") as runner:
                with self.assertRaisesRegex(ValueError, "incomplete"):
                    release.publish(directory, VERSION, self.env)
                self.assertEqual(runner.call_count, 1)
                self.assertIn("--draft", runner.call_args.args[0])

    def test_draft_missing_from_list_at_first_is_retried(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            file = directory / "asset.zip"
            file.write_bytes(b"asset")
            draft = {"tag_name": "v0.1.0", "assets": [{"name": file.name, "size": file.stat().st_size}]}
            responses = [{"object": {"sha": COMMIT}}, [], [], {}, [], [], [draft]]
            with patch.object(release, "verify_packages", return_value=[file]), patch.object(release, "gh_api", side_effect=responses), patch.object(release, "run") as runner, patch.object(release.time, "sleep") as sleep:
                release.publish(directory, VERSION, self.env)
                self.assertEqual(sleep.call_count, 2)
                self.assertIn("--draft=false", runner.call_args.args[0])

    def test_draft_that_never_appears_fails_without_publishing(self):
        with patch.object(release, "releases", return_value=[]), patch.object(release.time, "sleep"):
            with self.assertRaisesRegex(ValueError, "did not appear"):
                release.find_draft("v0.1.0", attempts=3)

    def test_success_pins_tag_to_tested_sha_and_publishes_after_upload(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            file = directory / "asset.zip"
            file.write_bytes(b"asset")
            responses = [{"object": {"sha": COMMIT}}, [], [], {}, [{"tag_name": "v0.1.0", "assets": [{"name": file.name, "size": file.stat().st_size}]}]]
            with patch.object(release, "verify_packages", return_value=[file]), patch.object(release, "gh_api", side_effect=responses) as api, patch.object(release, "run") as runner:
                release.publish(directory, VERSION, self.env)
                self.assertIn(unittest.mock.call("git/refs", "POST", {"ref": "refs/tags/v0.1.0", "sha": COMMIT}), api.call_args_list)
                create, publish = [call.args[0] for call in runner.call_args_list]
                self.assertIn("--verify-tag", create)
                self.assertIn("--draft", create)
                self.assertIn("--draft=false", publish)


if __name__ == "__main__":
    unittest.main()
