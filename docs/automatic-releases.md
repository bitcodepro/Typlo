# Automatic Windows releases

The immutable bootstrap is `v0.1.2` at
`1a46ca872083eb901916a2189d725ba8d8832d88`. The worker refuses remote writes
until that published release, tag, manifest and asset digests agree. The first
first-parent main commit after this anchor gets `0.1.3`, the second `0.1.4`, etc.
The infrastructure merge itself is an entry. Existing releases remain untouched.

## Version and source identity

No bot pushes to protected main and no additional credentials or permissions are
required. Main's Cargo version is the development baseline; it need not be bumped
for each PR. The worker creates a deterministic child commit of each source main
commit, changing only Cargo.toml and the application's Cargo.lock version. Both
binaries, the ZIP and manifest are built from that child commit. The release tag
points to that exact tested child, not to the unversioned source commit.

The manifest records `build_inputs`: immutable anchor, source main commit, ordinal,
both versioned Cargo blob SHAs, controller commit and preview flag. Commit identity,
message and source timestamp are fixed. The same source/ordinal always gives the
same version and release SHA. GitHub's returned blob/tree/commit SHAs must equal
the local tested objects before any draft or tag is created. Publication always
uses exact-commit mode; it cannot adopt a same-version release from another SHA.
The original publisher's verified same-version no-op remains available outside
the queue. Historical v0.1.0 manifest support remains unchanged.

## Durable queue and ordering

The durable queue is first-parent main history after the anchor, with verified
published releases as its completion ledger. Each push, scheduled run (twice an
hour), or manual run on main processes **one oldest unfinished entry**, bounded
by its event SHA. Retrying an already completed event creates nothing. A later
scheduled event recovers canceled, missing or overflowed wake-ups. The serialized
`windows-release` group uses `queue: max`; GitHub allows only 100 pending runs and
does not guarantee dispatch order. Correct ordering comes from the ledger scan,
not the order Actions runs start.

Squash merge and ordinary merge each produce one first-parent entry per PR.
Rebase merging a multi-commit PR produces one release per rebased commit; a direct
main push likewise releases each first-parent commit. This policy does not claim
one release per PR under every merge strategy. No history entry is silently
skipped: a failed oldest entry blocks subsequent ones until repaired. Rewritten
history, foreign drafts, conflicting tags, duplicate releases or tampered original
provenance stop the worker. Major/minor stay 0.1; patch is limited to 999999 by the
updater. Changing the numbering policy or bootstrap requires a reviewed migration.

Schedules can be delayed, disabled by GitHub inactivity rules, or unavailable when
Actions/quota is unavailable. One worker per wake-up trades throughput for bounded
job duration. A large backlog drains over subsequent wake-ups; recovery has no
delivery-time SLA. Ledger verification currently scales linearly with completed
entries and may eventually require a reviewed checkpoint strategy for API quotas.

## Gates, retry bytes and publication

An untrusted PR receives a local preview child commit and artifacts only, with a
read-only token. Preview numbering is hypothetical; concurrently merged main
changes can change its final ordinal. Main uses the same tests, clippy, static-CRT
build, runtime dependency/license checks, isolated UI smoke and package gates.
The one ZIP stays `typlo-win-x64.zip`; the updater accepts numeric X.Y.Z versions.
Linux/macOS release packages are not provided by this Windows workflow.

After all gates, the exact candidate (Cargo inputs, ZIP, checksums and manifest)
is saved under its release commit in the main-scoped cache, backed up as a
seven-day artifact, and cache persistence is checked before release writes.
Retries restore and validate those canonical bytes rather than replace assets
with a potentially different rebuild. Only the serialized main worker saves this
candidate cache. A cache hit reuses a previously gated candidate and verifies its
hashes and source/controller provenance; it does not claim a fresh build.

Publication exports only the tested Git objects, resumes/creates an owned draft by
release ID, verifies exact uploaded asset names and SHA256 digests, then publishes.
Existing assets are never deleted or overwritten. A missing cache plus a partial
draft may require manual recovery of the original artifact; different rebuilt
bytes fail closed. A dangling/non-uploaded asset or conflicting draft also needs
investigation rather than automatic deletion. Old release assets/tags are immutable.

## Verification scope

`tools/test-release-queue.ps1` uses isolated real Git histories for deterministic
version commits and concurrent branches, and mock ledgers for 130 merges,
out-of-order/lost wake-ups, reruns, baseline failures and foreign tags/drafts.
Git-object export is mocked and reconstructed with Git to check matching SHAs.
`tools/test-release-tools.ps1` covers digest/provenance validation and interrupted
draft upload/retry/no overwrite. These are offline contracts, not proof of a live
GitHub release or live updater installation. PR CI performs the full Windows
build/package/UI gates without publishing any test release or changing a profile.
