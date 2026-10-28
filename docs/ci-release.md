# CI and releases

The CI workflow checks pull requests and main on standard GitHub-hosted runners.
PR jobs have `contents: read`; only the final main-only publication job has
`contents: write`. There are no new secrets, paid runners, environments, repository
settings, or ruleset changes. All third-party actions are pinned to commit SHAs.

Job timeouts bound execution after a runner starts. They cannot guarantee runner
availability or bound GitHub's queue time, including macOS ARM capacity shortages.

## Checks and packages

- Restore, Release build and the unit tests run on Windows 2025, Ubuntu 24.04 and macOS 15.
- The Windows job also runs a local WebView2 contenteditable fixture through real copy/paste and
  keyboard input. It checks cross-process clipboard ownership, recent input beside old text,
  formatted paragraphs, Undo, and interruption by an unrelated process's copy. It also exercises
  native passive and transiently activated notification windows during a 300-character selection.
  Unit checks also cover cancellation on user input, persistent focus loss and lost selection ranges.
  The fixture uses isolated data/profile folders and sends no Teams messages. Its SDK is a test-only dependency;
  CI explicitly installs Microsoft's WebView2 Runtime if the runner does not already have it.
- The existing Linux end-to-end selftest runs inside a disposable Ubuntu/Xvfb/Openbox
  Docker container, with a read-only application mount, isolated writable XDG data/config
  directories and no network. It checks
  clipboard text/images, capture, hotkeys, layout conversion and encrypted storage.
  The report is uploaded even on failure; the test has a 180-second process timeout.
- Packaging runs natively for `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`,
  and `osx-x64`. Linux archives are `.tar.gz`; Windows and macOS archives are `.zip`.
  macOS archives contain `Typlo.app` and retain the existing ad-hoc signature.
  No signing identity or notarization is introduced.
- Every archive contains only the executable (or `Typlo.app`), README, MIT LICENSE,
  `THIRD-PARTY-NOTICES.txt` and `build-info.json` (version, RID and source commit).
  Linux packages additionally contain the restricted GNOME source-switching extension.
  The notices file lists the packages actually shipped for that RID and their license/notice
  texts, each identical text once.
  The verifier checks all five archive/checksum pairs, required contents, embedded
  source/version/RID, native executable architecture and Unix execute permissions.
  It rejects traversal, links, unexpected top-level entries, incomplete notices,
  missing platforms, extra assets and local data files.
- Test reports and PR preview packages are kept as Actions artifacts for seven days.

macOS Accessibility/Keychain, Linux Wayland behavior and the real Teams application
still need manual testing in a real user session. The Windows integration check covers
the WebView2 editor/process boundary; it does not sign into Teams or exercise its full UI.

The SDK is restricted to the .NET 10.0.4xx feature band with latest-patch roll-forward.
Unlike QueueLoom, Typlo currently has no NuGet lockfiles; restore uses the existing
fixed direct dependency versions. QueueLoom's broker/emulator tests, updater,
bootstrap launcher, image cache, artifact attestations and extra write permissions
are not part of this application or workflow.

## Version selection

The first automatic release is `v0.1.0`. Later main runs bump the highest stable
`vMAJOR.MINOR.PATCH` tag's patch by default. Only the first line of the main commit
message is inspected (the PR title when squash merging):

- `[minor]` or `#minor`: next minor, patch zero.
- `[major]` or `#major`: next major, minor/patch zero.
- `[skip release]`: run checks, but skip packages and publication on main.

Manual dispatch on main can select a bump or an explicit version such as `0.2.0-rc.1`.
Explicit versions override skip/bump; stable versions must increase. Numeric
prerelease identifiers cannot have leading zeros. Prereleases are marked as such
and do not become latest. A dispatch on another branch produces preview artifacts
only, as do pull requests (`0.0.0-pr.<number>`).

## First release after review and merge

1. Review and merge the workflow changes through a PR into protected main.
   This local preparation does not create that PR or push its branch.
2. A code/workflow push to main starts CI. All checks, five package builds and
   independent downloaded-package verification must pass before publication.
   Documentation-only pushes are ignored, as in QueueLoom.
3. With no existing version tags, CI selects `0.1.0`. Alternatively, after a
   `[skip release]` merge, manually run **CI** on **main** with `release_version=0.1.0`.
4. The publisher checks its workflow/event/repository/ref context, validates the
   packages again and requires current main to equal the tested commit. It then
   reserves the new immutable tag, creates a draft with all ten assets, checks the
   uploaded asset names/sizes and publishes the draft. It never moves existing tags.
5. Inspect the release's five archive/checksum pairs and install on real desktops.
   For example: `sha256sum -c Typlo-0.1.0-linux-x64.tar.gz.sha256`.

If main advanced during the build, that run cannot publish; use the current main's
run. If upload/publication fails after tag reservation, the immutable tag or draft
is retained for inspection. Do not delete/move the tag or blindly rerun the same
version; inspect the failed job/draft and choose a fresh version if necessary.

## Local verification

From the repository root:

```text
dotnet restore Typlo.slnx
dotnet build Typlo.slnx -c Release --no-restore
dotnet test Typlo.slnx -c Release --no-build
python -m pip install PyYAML==6.0.3
python -B -m unittest discover -s tests/release -v
python scripts/release.py package --rid win-x64 --version 0.1.0 --directory dist/local-packages
python scripts/release.py verify --rid win-x64 --version 0.1.0 --directory dist/local-packages --commit <git-HEAD>
```

Use an empty output directory. Local packaging may cross-publish Windows/Linux,
but macOS bundles must be assembled and ad-hoc signed on macOS. None of these
commands publishes anything; `publish` refuses a normal local session. The legacy
`scripts/publish-all.sh` and `scripts/package-macos.sh` remain available for local
unversioned builds; CI uses `scripts/release.py` for verified release artifacts.


Windows packages use Native AOT and include the five native libraries recorded by the verifier.
The manifest marks this deployment as `native-aot`; verification checks the DLL set and PE architecture,
while still recognizing older single-file package manifests. Windows CI additionally publishes Native
AOT, exercises on-demand UI shutdown/crash/restart, shortcut restoration, screenshot cancellation,
encrypted image ingestion, idle memory reclamation, and the isolated update helper against a local feed.
It also hides history during delayed OCR, cancels OCR on UI shutdown, checks that no plaintext PNG is
written to the temporary directory, and verifies tray history targets the external editor while Settings
belongs to the foreground UI child. A real Windows OCR check reads its PNG from stdin; cancellation must
kill and reap the owned helper.
The runtime test uses its own profile, does not monitor external clipboard changes, and never replaces
the user's clipboard. Resident memory samples are retained in `runtime/runtime.txt`. The same AOT binary
also runs the existing desktop self-test, separately from the WebView2 editor integration test.

Windows typing uses one native hook for each platform lifetime. Disabling input tracking gates
event delivery; platform disposal stops the hook and joins its owning thread before returning.
An isolated native check exercises ten platform lifetimes, alternating immediate disposal during
startup with disposal after native readiness, including double disposal of each platform.
libuiohook's final quit-message post can report Failure after its first close message already
terminated the hook. That result is accepted only after the owning thread has joined and
`Run` returned successfully; a live or faulted hook still fails the check.
The WebView2 integration waits for native hook readiness and actual typed-key/browser delivery,
then verifies recent-input conversion through rapid enable/disable and controller replacement.
Conversion and Undo assertions remain exact; hook errors include their full stack traces.
