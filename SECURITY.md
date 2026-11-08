# Security policy

## Supported versions

Only the latest release on the [Releases page](https://github.com/tether-works/IgnisDisc/releases) receives fixes.

## Reporting a vulnerability

Please report security problems privately, not in a public issue:

1. Open the [Security tab](https://github.com/tether-works/IgnisDisc/security) of this repository.
2. Click **Report a vulnerability** and describe the problem, the affected version, and how to reproduce it.

You will get a reply in the advisory thread. Fixes ship in a new release (published files are never replaced), and the
advisory is published once a fixed version is available.

## Verifying downloads

Every release lists SHA-256 checksums for its files, both on the release page and on
[ignisdisc.tetherworks.dev](https://ignisdisc.tetherworks.dev). Builds are not code-signed yet; compare the checksum
before running a file you downloaded from anywhere else.

## Code signing policy

Free code signing is provided by [SignPath.io](https://signpath.io), certificate by the
[SignPath Foundation](https://signpath.org) (application and CI pipeline configured; current v1.10 binaries are
unsigned until certificate provisioning completes).

- **Committers, reviewers and approvers:** [tether-works](https://github.com/tether-works) maintainers. External pull
  requests require maintainer review before merge, and release builds run only on GitHub-hosted runners from tagged
  commits in `.github/workflows/release.yml`.

## Privacy policy

IgnisDisc does not collect telemetry, analytics, crash reports, or usage data. This program will not transfer any
information to other networked systems unless specifically requested by the user or the person installing or operating
it:

- **MusicBrainz lookup (opt-in):** When you click *Look Up Metadata* or pass `--lookup` to `ignisdisc-cli rip-audio`,
  the disc's TOC sector offsets are sent to `musicbrainz.org` to fetch album and track titles.
- **AccurateRip verification (opt-in):** When you enable *Verify with AccurateRip* or pass `--verify-accuraterip` /
  `detect-offset`, the disc's TOC identifiers are used to download the matching checksum bin from
  `accuraterip.com`.

