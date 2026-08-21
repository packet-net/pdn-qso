# CLAUDE.md

Operating notes for agents working in `packet-net/pdn-qso`.

## What this repo is

`pdn-qso` is a terminal tool for interactive two-way testing over the pdn-soundmodem modems: a
Monitor mode, keyboard-to-keyboard Chat with a stop-and-wait ARQ, fountain-coded File transfer,
and a Perf mode that produces numbers. Three devices: FlexRadio (DAX audio + PTT + power), a
sound card with the CM108 PTT widget, and an UberSDR web receiver (receive only). Read
[docs/plan.md](docs/plan.md) for the why and [docs/design.md](docs/design.md) for the how; the
design doc is binding on layout, protocol and conventions.

## Licence rules (hard)

- This repo is **AGPL-3.0-or-later**. It depends on `pdn-soundmodem` (GPL-3.0-or-later, allowed
  under GPLv3 §13) and on `M0LTE.Flex` (AGPL-3.0). Never copy source from pdn-soundmodem into
  this repo; consume the published NuGet package by its public API. New dependencies must be
  AGPL-compatible (MIT/Apache-2.0/BSD/LGPL/GPL/AGPL are fine).
- Provenance: an algorithm ported from a paper or another project gets a comment naming it.
  The LT fountain code is implemented from Luby's paper and MacKay's description of the robust
  soliton distribution; RaptorQ is deliberately not used.

## Conventions (mirror pdn-soundmodem)

- net10.0, C# latest, nullable + warnings-as-errors, Central Package Management
  (`Directory.Packages.props` - no `Version=` on a `PackageReference`).
- Tests: xunit v3 + AwesomeAssertions (never FluentAssertions), test names
  `Snake_Case_Like_Sentences`, one test project. Wall-clock through `TimeProvider` only in
  library code; the UI may use the clock directly.
- **No wall clock in a test, ever.** No `Task.Delay`, no `DateTime.UtcNow`, no
  `WaitAsync(timeout)`, no `CancellationTokenSource(TimeSpan)`, no "try a hundred times with a
  20 ms gap". Nothing a test asserts may depend on how busy the machine is. Instead:
  `VirtualClock` is the `TimeProvider` every station, session, transfer and perf run in a test
  is given, and `VirtualTime` drives it - `WaitForAsync` for a fact (no deadline: a test that
  hangs is a finding the runner reports, where "not within five seconds on this box" is not),
  `RunAsync`/`UntilAsync` to let protocol timeouts fire without waiting for them in real life.
  A rig subscribing `AudioLink.Carried` to the clock makes transmitting cost its own air time.
  Anything the clock must not be run past has to say so from the instant the work is taken on:
  see `AudioLink.Carrying`, `ChatSession.Sending`, `FileReceiver.Busy`.
- Hot paths (anything per audio block or per symbol): no steady-state allocation, no LINQ.
- **No em dashes or en dashes anywhere** - code, comments, docs, commit messages, PR bodies.
  Hyphen, comma, semicolon or full stop. Printable strings stay ASCII (`->` not an arrow).
- CI: every workflow job targets `[self-hosted, Linux, X64]`. PRs merge on green CI; fix
  forward.
- **A PR title is a release-note bullet**: one plain, user-facing line with a
  `feat:`/`fix:`/`docs:`/`test:`/`chore:` prefix; detail in the body. Releases are `v*` tags;
  `release.yml` builds the `.deb`s and writes the notes from PR titles.
- Run targeted tests with the test exe and `-class`/`-method` (xunit v3 on Microsoft.Testing
  Platform ignores `dotnet test --filter`).

## What lives where

```
src/PdnQso.Link/      protocol layer, no UI: frames, ARQ, fountain code, perf, frame log
src/PdnQso/           the terminal application (Terminal.Gui): devices, settings, modes
tests/PdnQso.Tests/   one test project, hermetic (two-station rigs through an in-process channel)
debian/               packaging for the .deb
docs/                 plan.md, design.md
```

The reference implementation of everything this tool sits on is the pdn-soundmodem source,
checked out read-only at `/home/tf/pdn-soundmodem` on the development box. Read it to learn the
API; never copy it.
