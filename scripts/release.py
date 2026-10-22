"""Typlo version selection, isolated packaging, verification and main-only publication.

Only the publish subcommand writes to GitHub. No subcommand moves existing tags.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import plistlib
import re
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[1]
RIDS = ("win-x64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64")
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")


def run(args, **kwargs):
    return subprocess.run(args, cwd=ROOT, check=True, **kwargs)


def version_tuple(version):
    match = SEMVER.fullmatch(version)
    if not match or (match[4] and any(re.fullmatch(r"0[0-9]+", part) for part in match[4].split("."))):
        raise ValueError("Expected SemVer without v or build metadata (e.g. 0.1.0 or 0.2.0-rc.1)")
    # .NET assembly versions and CFBundleVersion must be representable too.
    parts = tuple(map(int, match.groups()[:3]))
    if any(part > 65534 for part in parts):
        raise ValueError("Version components must fit .NET assembly versions")
    return parts


def select_version(tags, message="", bump="auto", explicit=""):
    stable = [version_tuple(tag[1:]) for tag in tags if re.fullmatch(r"v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", tag)]
    latest = max(stable, default=None)
    subject = message.splitlines()[0] if message else ""
    if explicit:
        parts = version_tuple(explicit)
        if "v" + explicit in tags:
            raise ValueError("Release tag already exists; immutable tags are never reused")
        if "-" not in explicit and latest is not None and parts <= latest:
            raise ValueError("Stable version must exceed the highest stable tag")
        version = explicit
    else:
        if "[skip release]" in subject.lower():
            return {"skip": "true"}
        if bump == "auto":
            bump = "major" if re.search(r"\[major\]|#major", subject, re.I) else "minor" if re.search(r"\[minor\]|#minor", subject, re.I) else "patch"
        if latest is None:
            parts = (0, 1, 0)
        elif bump == "major":
            parts = (latest[0] + 1, 0, 0)
        elif bump == "minor":
            parts = (latest[0], latest[1] + 1, 0)
        elif bump == "patch":
            parts = (latest[0], latest[1], latest[2] + 1)
        else:
            raise ValueError("Unknown version bump")
        version = ".".join(map(str, parts))
        version_tuple(version)
        if "v" + version in tags:
            raise ValueError("Release tag already exists")
    return {"skip": "false", "version": version, "tag": "v" + version, "prerelease": str("-" in version).lower()}


def outputs(values):
    for key, value in values.items():
        print(f"{key}={value}")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            for key, value in values.items():
                stream.write(f"{key}={value}\n")


def extension(rid):
    return "tar.gz" if rid.startswith("linux-") else "zip"


def archive_name(version, rid):
    version_tuple(version)
    if rid not in RIDS:
        raise ValueError("Unsupported RID")
    return f"Typlo-{version}-{rid}.{extension(rid)}"


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def executable_path(rid):
    if rid.startswith("osx-"):
        return "Typlo.app/Contents/MacOS/Typlo"
    return "Typlo.exe" if rid == "win-x64" else "Typlo"


def validate_executable(header, rid):
    if rid == "win-x64":
        offset = struct.unpack_from("<I", header, 60)[0] if len(header) >= 64 else 0
        valid = header[:2] == b"MZ" and offset >= 64 and header[offset:offset + 6] == b"PE\0\0\x64\x86"
    elif rid.startswith("linux-"):
        valid = len(header) >= 20 and header[:6] == b"\x7fELF\x02\x01" and struct.unpack_from("<H", header, 18)[0] == (183 if rid == "linux-arm64" else 62)
    else:
        valid = len(header) >= 8 and header[:4] == b"\xcf\xfa\xed\xfe" and struct.unpack_from("<I", header, 4)[0] == (0x100000C if rid == "osx-arm64" else 0x1000007)
    if not valid:
        raise ValueError(f"Executable architecture does not match {rid}")


def safe_entry(name):
    path = PurePosixPath(name)
    if not name or "\\" in name or ":" in name or path.is_absolute() or ".." in path.parts:
        raise ValueError(f"Unsafe archive path: {name}")
    lower = tuple(part.lower() for part in path.parts)
    if any(part in (".git", "data", ".env", "obj", "bin") or part.endswith(".dsym") for part in lower) or path.suffix.lower() in (".key", ".pem", ".pfx", ".p12", ".db", ".sqlite", ".sqlite3", ".log", ".pdb", ".dbg"):
        raise ValueError(f"Local data or build residue in package: {name}")


def verify_archive(archive, version, rid, commit):
    expected = archive_name(version, rid)
    if archive.name != expected:
        raise ValueError("Archive filename mismatch")
    checksum = archive.with_name(archive.name + ".sha256")
    line = checksum.read_text(encoding="ascii").strip()
    if line != f"{digest(archive)}  {expected}":
        raise ValueError(f"SHA-256 mismatch or invalid checksum filename: {expected}")
    required = {"README.md", "LICENSE", NOTICES, "build-info.json", executable_path(rid)}
    if rid.startswith("osx-"):
        required.add("Typlo.app/Contents/Info.plist")
    with zipfile.ZipFile(archive) if extension(rid) == "zip" else tarfile.open(archive, "r:gz") as bundle:
        entries = bundle.infolist() if isinstance(bundle, zipfile.ZipFile) else bundle.getmembers()
        files = {}
        for entry in entries:
            name = entry.filename if isinstance(bundle, zipfile.ZipFile) else entry.name
            safe_entry(name)
            if name in files:
                raise ValueError("Duplicate archive entry")
            mode = entry.external_attr >> 16 if isinstance(bundle, zipfile.ZipFile) else entry.mode
            is_dir = entry.is_dir() if isinstance(bundle, zipfile.ZipFile) else entry.isdir()
            if isinstance(bundle, zipfile.ZipFile):
                if stat.S_ISLNK(mode):
                    raise ValueError("Symlinks are not allowed in packages")
            elif not (entry.isfile() or is_dir):
                raise ValueError("Links/devices are not allowed in packages")
            if not is_dir:
                files[name] = entry
        if not required <= files.keys():
            raise ValueError(f"Missing package entries: {sorted(required - files.keys())}")
        extra = sorted({name.split("/")[0] for name in files} - TOP_LEVEL)
        if extra:
            raise ValueError(f"Unexpected top-level package entries: {extra}")

        def read(name, limit=65536):
            stream = bundle.open(files[name]) if isinstance(bundle, zipfile.ZipFile) else bundle.extractfile(files[name])
            with stream:
                return stream.read(limit)

        manifest = json.loads(read("build-info.json"))
        if manifest != {"version": version, "rid": rid, "commit": commit}:
            raise ValueError("Package source/version/RID mismatch")
        if b"MIT License" not in read("LICENSE") or b"Copyright (c) 2026 bitcodepro" not in read("LICENSE"):
            raise ValueError("Missing Typlo MIT copyright")
        notices = read(NOTICES, 16 * 1024 * 1024)
        if not all(component in notices for component in (b"Avalonia", b"SkiaSharp", b"License for:")):
            raise ValueError("Third-party notices are incomplete")
        validate_executable(read(executable_path(rid), 8192), rid)
        executable = files[executable_path(rid)]
        mode = executable.external_attr >> 16 if isinstance(bundle, zipfile.ZipFile) else executable.mode
        if rid != "win-x64" and not mode & 0o111:
            raise ValueError("Unix executable permission is missing")
        if rid.startswith("osx-"):
            info = plistlib.loads(read("Typlo.app/Contents/Info.plist"))
            if info.get("CFBundleShortVersionString") != version.split("-")[0] or info.get("CFBundleExecutable") != "Typlo":
                raise ValueError("Invalid macOS bundle version/executable")
    print(f"PASS {expected}: SHA-256, contents, source, version and architecture")


def verify_packages(directory, version, commit, rids=RIDS):
    expected = {name for rid in rids for name in (archive_name(version, rid), archive_name(version, rid) + ".sha256")}
    actual = {path.name for path in directory.iterdir()}
    if actual != expected:
        raise ValueError(f"Expected exactly the archive/checksum pairs; missing={sorted(expected - actual)}, extra={sorted(actual - expected)}")
    for rid in rids:
        verify_archive(directory / archive_name(version, rid), version, rid, commit)
    return [directory / name for name in sorted(expected)]


NOTICES = "THIRD-PARTY-NOTICES.txt"
# Everything an archive may contain at its top level: no extra folders.
TOP_LEVEL = {"README.md", "LICENSE", NOTICES, "build-info.json", "Typlo.exe", "Typlo", "Typlo.app"}
LICENSE_FILE = re.compile(r"(?i)^(license|licence|copying)([._-].*)?(\.txt|\.md)?$")
NOTICE_FILE = re.compile(r"(?i)^(third[-_]?party[-_]?notices?|notice)(\.txt|\.md)?$")


def shipped_packages(assets, rid):
    """Packages whose runtime or native files end up in the published app for this RID."""
    target = assets["targets"].get(f"net10.0/{rid}") or assets["targets"]["net10.0"]
    families = {rid, rid.split("-")[0]} | ({"unix"} if not rid.startswith("win") else set())
    shipped = []
    for key, entry in target.items():
        if entry.get("type") != "package":
            continue
        files = list(entry.get("runtime", {})) + list(entry.get("native", {}))
        files += [f for f in entry.get("runtimeTargets", {}) if f.split("/")[1] in families]
        if any(not f.endswith("/_._") for f in files):
            shipped.append(key)
    return sorted(shipped, key=str.lower)


def package_texts(assets, key):
    """License and notice texts found in a restored package, plus its declared license as a fallback."""
    library = assets["libraries"][key]
    for folder in assets["packageFolders"]:
        package = Path(folder) / library["path"]
        if not package.is_dir():
            continue
        licenses, notices = [], []
        for file in sorted(package.rglob("*")):
            if not file.is_file() or file.suffix.lower() not in ("", ".txt", ".md"):
                continue
            if LICENSE_FILE.match(file.name):
                licenses.append(file.read_text(encoding="utf-8", errors="replace").strip())
            elif NOTICE_FILE.match(file.name):
                notices.append(file.read_text(encoding="utf-8", errors="replace").strip())
        declared = None
        nuspec = next(package.glob("*.nuspec"), None)
        if nuspec is not None:
            text = nuspec.read_text(encoding="utf-8", errors="replace")
            match = re.search(r"<license[^>]*>([^<]+)</license>", text) or re.search(r"<licenseUrl>([^<]+)</licenseUrl>", text)
            declared = match.group(1).strip() if match else None
        return list(dict.fromkeys(licenses)), list(dict.fromkeys(notices)), declared
    return [], [], None


def collect_notices(staging, rid):
    """One THIRD-PARTY-NOTICES.txt for the packages actually shipped for this RID; identical texts appear once."""
    assets = json.loads((ROOT / "src/Typlo.App/obj/project.assets.json").read_text(encoding="utf-8-sig"))
    rule = "=" * 78
    components, by_license, by_notice = [], {}, {}
    for key in shipped_packages(assets, rid):
        name, version = key.split("/")
        licenses, notices, declared = package_texts(assets, key)
        components.append(f"  {name} {version}" + (f"  ({declared})" if declared else ""))
        for text in licenses:
            by_license.setdefault(text, []).append(name)
        if not licenses and declared:
            by_license.setdefault(f"License: {declared}", []).append(name)
        for text in notices:
            by_notice.setdefault(text, []).append(name)

    lines = [
        "Typlo third-party notices",
        "",
        "Typlo is MIT-licensed (see LICENSE). It includes the open-source components below; their licenses",
        "still apply to them. Identical license texts are listed once with all components that use them.",
        "",
        rule, "Components in this build", rule,
        *components,
        "  .NET runtime  (MIT) https://github.com/dotnet/runtime/blob/main/LICENSE.TXT",
        "    .NET third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT",
        "",
    ]
    for text, names in by_license.items():
        lines += [rule, "License for: " + ", ".join(dict.fromkeys(names)), rule, text, ""]
    for text, names in by_notice.items():
        lines += [rule, "Notices for: " + ", ".join(dict.fromkeys(names)), rule, text, ""]
    (staging / NOTICES).write_text("\n".join(lines), encoding="utf-8")


def package(directory, version, rid):
    name = archive_name(version, rid)
    directory = directory.resolve()
    if not directory.is_relative_to(ROOT / "dist"):
        raise ValueError("Package output must be inside this checkout's dist/ directory")
    if rid.startswith("osx-") and sys.platform != "darwin":
        raise ValueError("macOS bundles require a macOS package host")
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / name
    if archive.exists() or archive.with_name(name + ".sha256").exists():
        raise FileExistsError("Use an empty output directory; packages are not silently replaced")
    commit = run(["git", "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    with tempfile.TemporaryDirectory(prefix=f".package-{rid}-", dir=directory.parent) as temporary:
        work = Path(temporary)
        publish, staging = work / "publish", work / "staging"
        staging.mkdir()
        run(["dotnet", "publish", "src/Typlo.App/Typlo.App.csproj", "-c", "Release", "-r", rid, "--self-contained", "true", "-o", str(publish), f"-p:Version={version}", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=none", "-p:DebugSymbols=false"])
        destination = staging / "Typlo.app/Contents/MacOS" if rid.startswith("osx-") else staging
        destination.mkdir(parents=True, exist_ok=True)
        for file in publish.rglob("*"):
            if file.is_file():
                relative = file.relative_to(publish)
                # Some native NuGet packages publish PDBs regardless of DebugSymbols=false.
                if file.suffix.lower() in (".pdb", ".dbg") or any(part.lower().endswith(".dsym") for part in relative.parts):
                    continue
                safe_entry(relative.as_posix())
                target = destination / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, target)
        for doc in ("README.md", "LICENSE"):
            shutil.copyfile(ROOT / doc, staging / doc)
        collect_notices(staging, rid)
        (staging / "build-info.json").write_text(json.dumps({"version": version, "rid": rid, "commit": commit}) + "\n", encoding="utf-8")
        executable = staging / executable_path(rid)
        executable.chmod(executable.stat().st_mode | 0o111)
        if rid.startswith("osx-"):
            info = {"CFBundleName": "Typlo", "CFBundleDisplayName": "Typlo", "CFBundleIdentifier": "pro.typlo.app", "CFBundleExecutable": "Typlo", "CFBundlePackageType": "APPL", "CFBundleVersion": version.split("-")[0], "CFBundleShortVersionString": version.split("-")[0], "LSMinimumSystemVersion": "12.0", "LSUIElement": True, "NSHighResolutionCapable": True}
            (staging / "Typlo.app/Contents/Info.plist").write_bytes(plistlib.dumps(info))
            # Preserve the existing package-macos.sh ad-hoc signature; no developer identity/notarization.
            run(["codesign", "--force", "--deep", "--sign", "-", str(staging / "Typlo.app")])
            run(["codesign", "--verify", "--deep", "--strict", str(staging / "Typlo.app")])
        if extension(rid) == "zip":
            with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as bundle:
                for file in sorted(staging.rglob("*")):
                    if file.is_file():
                        bundle.write(file, file.relative_to(staging).as_posix())
        else:
            with tarfile.open(archive, "x:gz") as bundle:
                for file in sorted(staging.rglob("*")):
                    if file.is_file():
                        relative = file.relative_to(staging).as_posix()
                        entry = bundle.gettarinfo(str(file), arcname=relative)
                        # Windows cannot represent Unix execute bits during cross-publishing.
                        entry.mode = 0o755 if relative == executable_path(rid) else 0o644
                        with file.open("rb") as stream:
                            bundle.addfile(entry, stream)
    archive.with_name(name + ".sha256").write_text(f"{digest(archive)}  {name}\n", encoding="ascii")
    verify_archive(archive, version, rid, commit)
    outputs({"archive": archive.as_posix(), "checksum": archive.as_posix() + ".sha256"})


def gh_api(path, method="GET", data=None):
    args = ["gh", "api", "--method", method, f"repos/bitcodepro/Typlo/{path}"]
    if data is not None:
        args += ["--input", "-"]
    response = run(args, input=None if data is None else json.dumps(data), capture_output=True, text=True)
    return json.loads(response.stdout)


def releases():
    result = []
    page = 1
    while True:
        batch = gh_api(f"releases?per_page=100&page={page}")
        result.extend(batch)
        if len(batch) < 100:
            return result
        page += 1


def find_draft(tag, attempts=10, delay=3.0):
    """The release list is eventually consistent: a just-created draft can be missing for a few seconds.
    (The tag endpoint only promises published releases, so the list is the only way to see our draft.)"""
    for attempt in range(attempts):
        release = next((item for item in releases() if item["tag_name"] == tag), None)
        if release is not None:
            return release
        if attempt < attempts - 1:
            time.sleep(delay)
    raise ValueError(f"Draft for {tag} did not appear in the release list; draft retained for inspection")


def publish(directory, version, env):
    commit = env.get("GITHUB_SHA", "")
    if (env.get("GITHUB_ACTIONS") != "true" or env.get("GITHUB_REPOSITORY") != "bitcodepro/Typlo" or env.get("GITHUB_REF") != "refs/heads/main" or env.get("GITHUB_EVENT_NAME") not in ("push", "workflow_dispatch") or env.get("GITHUB_WORKFLOW_REF") != "bitcodepro/Typlo/.github/workflows/ci.yml@refs/heads/main" or not re.fullmatch(r"[0-9a-f]{40}", commit)):
        raise ValueError("Publication requires Typlo's CI workflow on main with an exact source SHA")
    version_tuple(version)
    files = verify_packages(directory, version, commit)
    if gh_api("git/ref/heads/main")["object"]["sha"] != commit:
        raise ValueError("main moved since this build; publish from the current main run")
    tags = [ref["ref"].removeprefix("refs/tags/") for ref in gh_api("git/matching-refs/tags/v")]
    select_version(tags, explicit=version)  # Refuse collisions and stable version regression before writing.
    tag = "v" + version
    if any(item["tag_name"] == tag for item in releases()):
        raise ValueError("A release/draft already exists for this version; inspect it instead of overwriting")
    gh_api("git/refs", "POST", {"ref": f"refs/tags/{tag}", "sha": commit})
    args = ["gh", "release", "create", tag, "--repo", "bitcodepro/Typlo", "--verify-tag", "--target", commit, "--title", f"Typlo {tag}", "--generate-notes", "--draft"]
    if "-" in version:
        args.append("--prerelease")
    run(args + [str(file) for file in files])
    release = find_draft(tag)
    actual = {asset["name"]: asset["size"] for asset in release["assets"]}
    if actual != {file.name: file.stat().st_size for file in files}:
        raise ValueError("Release asset upload is incomplete; draft retained for inspection")
    run(["gh", "release", "edit", tag, "--repo", "bitcodepro/Typlo", "--draft=false", f"--latest={str('-' not in version).lower()}"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("version")
    for name in ("package", "verify", "publish"):
        sub = commands.add_parser(name)
        sub.add_argument("--directory", type=Path, default=ROOT / "dist/packages")
        sub.add_argument("--version", required=True)
        if name == "package":
            sub.add_argument("--rid", choices=RIDS, required=True)
        elif name == "verify":
            sub.add_argument("--rid", choices=RIDS)
            sub.add_argument("--commit", required=True)
    args = parser.parse_args()
    if args.command == "version":
        tags = run(["git", "tag", "--list", "v*"], capture_output=True, text=True).stdout.splitlines()
        message = os.environ.get("COMMIT_MESSAGE") or run(["git", "log", "-1", "--format=%s"], capture_output=True, text=True).stdout
        outputs(select_version(tags, message, os.environ.get("BUMP", "auto"), os.environ.get("RELEASE_VERSION", "")))
    elif args.command == "package":
        package(args.directory, args.version, args.rid)
    elif args.command == "verify":
        verify_packages(args.directory, args.version, args.commit, (args.rid,) if args.rid else RIDS)
    else:
        publish(args.directory, args.version, os.environ)


if __name__ == "__main__":
    main()
