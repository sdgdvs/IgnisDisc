# Contributing to IgnisDisc

Thanks for helping. The most valuable contributions right now are **reports from real drives and discs**: IgnisDisc's
automated tests run against a built-in virtual drive, so results from real hardware are how problems get found.

## Reporting results from your drive

Open a [drive compatibility report](https://github.com/tether-works/IgnisDisc/issues/new?template=drive_report.yml).
Include the output of:

```powershell
ignisdisc-cli drives --json
ignisdisc-cli media-info --drive D: --json
```

Reports of things that **worked** are useful too.

## Bugs and feature requests

- Bugs: use the [bug report](https://github.com/tether-works/IgnisDisc/issues/new?template=bug_report.yml) form.
- Ideas: check the [roadmap section in README.md](README.md#roadmap) first, then open a discussion or issue.
- Security problems: see [SECURITY.md](SECURITY.md) — please don't file them as public issues.

## Code changes

Requirements: Windows 10 or 11 (x64) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build CdBurner.sln -c Release
dotnet test  CdBurner.sln -c Release --no-build
```

A pull request is ready when:

- the build has **0 warnings and 0 errors**, and every test passes (CI checks both);
- new behaviour and bug fixes come with tests (the virtual drive in `CdBurner.Hardware.Simulation` lets you test
  burning and ripping without hardware);
- user-facing text and docs state only what the software actually does.

Anything that writes to real discs in a new way needs real-hardware testing before it ships; say in the PR which
drives and media you tested.

By contributing you agree that your contributions are licensed under the [GNU General Public License v3.0 or later](LICENSE).
