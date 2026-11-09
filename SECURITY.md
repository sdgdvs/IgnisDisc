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

## Code signing

Releases are not code-signed yet, so Windows SmartScreen may warn when you first run them. Signing through the
[SignPath Foundation](https://signpath.org) programme for open-source projects is planned; until a signed release is
published, compare the SHA-256 checksum above. Release builds are produced from tagged commits by
`.github/workflows/release.yml` on GitHub-hosted runners, and changes from outside contributors are merged only after
maintainer review.

## Privacy

IgnisDisc collects no telemetry, analytics, crash reports or usage data. It makes network requests only in these
cases:

- **MusicBrainz lookup** (audio CD ripping; *on by default* in the app's rip options, off unless you pass `--lookup`
  on the command line): the disc's table of contents (track start positions) is sent to `musicbrainz.org` to fetch
  album and track titles.
- **AccurateRip check** (audio CD ripping; *on by default* in the app's rip options, off unless you pass
  `--verify-accuraterip` or run `detect-offset` on the command line): IDs computed from the disc's table of contents
  are used to download the matching checksum file from `accuraterip.com`.
- **Update check** (*off by default*): when you turn on *Check for updates on startup* or click
  *Check for Updates Now* in Settings, the app asks `api.github.com` for the latest IgnisDisc release. No personal or
  machine identifiers are sent.

Both ripping lookups can be switched off in the rip options; the choice is remembered.
