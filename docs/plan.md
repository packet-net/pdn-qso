# pdn-qso - a hand-off tool for interactive two-way testing

**Status: plan, 2026-08-21. No code exists.** Written at Tom's request for a tool somebody else can be handed: a small terminal UI over the pdn-soundmodem modems that lets two stations talk, move a file and measure a link, with no AX.25 node in the way.

## 1. What it is

A single self-contained program (`pdn-qso`, name open) with a full-screen terminal UI, driving one radio through one of three devices, in one of four modes of use:

| Mode | What you see | What goes on air |
|---|---|---|
| **Monitor** | every frame heard: time, callsigns, modem, SNR, carrier offset, quality, the payload as text or hex | nothing |
| **Chat** | a two-pane keyboard-to-keyboard conversation with delivery ticks | short acknowledged messages |
| **File** | progress of a transfer in either direction, with the receiver's "have / need" count | rateless (fountain-coded) blocks and small status frames |
| **Perf** | a table of numbers: frames sent / heard / delivered, goodput, mean and worst SNR, round-trip time, time on air | a scripted stream of numbered frames, or a chat-style ping-pong |

Devices: **Flex** (6000-series over the LAN: DAX audio + PTT, power settable), **CM108** (any sound card plus the CM108 GPIO PTT widget, the bench rig), **UberSDR** (a public web receiver, receive only: Monitor mode, or the receive half of a two-way test with a transmitter elsewhere).

Settings are few and all visible on one screen: device, modem mode, audio centre, callsign, TX delay, audio levels, Flex power, ident interval. First run is a short wizard; after that a JSON file under `~/.config/pdn-qso/`.

## 2. What it is not

Not a node, not a BBS, not Winlink, not a replacement for the daemon. No routing, no multi-station sessions, no persistent message store. One radio, one correspondent (plus everyone listening in Monitor). It is a test instrument with a friendly face.

## 3. How it is built

A new repository, `packet-net/pdn-qso`, **GPL-3.0-or-later** (it depends on pdn-soundmodem, which is GPL), consuming the published `pdn-soundmodem` NuGet package by its public API - never a copy of the source - and cut as a `.deb` for amd64/arm64/armhf by the same release pipeline shape this repo uses (tests, self-contained publish, release notes from PR titles).

Four layers, each testable without the one above:

1. **Device** - the library's `AlsaPcm` + `Cm108Ptt`/`SerialPtt`, `FlexDevice`/`FlexRuntime` (DAX + PTT + power), `UberSdrDevice` (receive only), and the library's `pipe:` device for two instances talking through named pipes on one machine. Nothing new here beyond a small adapter that maps the wizard's choices onto the library's device strings (the same `alsa:`/`flex:`/`ubersdr:`/`pipe:` forms `CONFIG.md` documents).
2. **Modem** - `ModemCatalog.Create(mode, rate, sink, options)` gives an `IModem` for any shipped mode (the packet modes, the FreeDV datac modes, the MS110D waveforms), with `FrameDecoded` carrying the payload and its `FrameQuality` (SNR, offset, erasures, chase bits). Busy detection and DCD come from the library too. Centre frequency through `ModemOptions`; MS110D's transmit waveform switchable at runtime through `IHardwareControllable`.
3. **Link** - the tool's own small protocol, carried as ordinary AX.25 UI frames inside the modem's IL2P framing so that every existing monitor, node and the daemon's own frame log see well-formed traffic, and the tool coexists on a shared channel. One-byte type + session id + sequence, then:
   - *Chat*: stop-and-wait ARQ - send, wait for the ack, retry with a DCD-respecting backoff, give up after N and say so. Delivery ticks in the UI. Simple on purpose: the modem's frames already carry a CRC, and a chat line is one frame.
   - *File*: a fountain code - LT coding with a robust soliton degree distribution, systematic first pass (the K source blocks go out first, so a clean link needs no repair at all), then repair symbols until the receiver reports complete. The receiver sends a tiny "have n of K" status every few seconds and a final ack; the sender never needs to know which blocks were lost. Blocks are sized to the mode's frame capacity; a whole-file CRC-32 closes the transfer. (LT's original patent has expired; RaptorQ is deliberately avoided.)
   - *Perf*: a numbered stream of fixed-size frames for one-way statistics, and a ping-pong using the chat ARQ for round-trip time. Both ends display the same numbers: sent, heard, delivered, frame error rate, goodput (payload bytes per second of air time), mean / worst SNR, RTT, and the modem mode and centre they were taken at, so a screenshot is a complete measurement.
   - *Monitor*: no protocol - every `FrameDecoded` rendered, including frames that are not the tool's.
4. **UI** - Terminal.Gui (MIT) - a status bar (device, mode, centre, PTT/DCD lamps, SNR of the last frame), a main pane per mode, a settings dialog, and a log pane. Keyboard only; works over SSH; nothing below 80x24.

## 4. Testing, without a radio first

The library's `pipe:` device and its Watterson channel give a two-station rig on one machine: two `pdn-qso` instances (or two link-layer objects in a test) connected through a simulated HF channel at a chosen SNR. Every protocol claim - the ARQ delivers or gives up honestly, the fountain transfer completes at the predicted symbol count, the perf numbers agree with what was sent - is pinned hermetically that way before anything goes near a transmitter. Then the CM108 bench rig (radio2) for the device layer, then Flex-to-UberSDR one way, then two stations.

## 5. Phases

| Phase | Delivers | Size |
|---|---|---|
| **A - skeleton** | repo, licence, CI/release pipeline, the device adapter for the three devices plus `pipe:`, Monitor mode end to end, the settings screen and first-run wizard | the biggest phase: most of the plumbing |
| **B - chat** | the link frame format, stop-and-wait ARQ, the chat pane with delivery ticks, hermetic two-station tests through the channel rig | small |
| **C - file** | the LT fountain coder (its own tested unit), the transfer protocol, the progress pane, tests that predict and measure the repair overhead at several SNRs | medium |
| **D - perf** | the stream and ping-pong tests, the numbers pane, a one-line export of each run (CSV and a text summary) | small |
| **E - hand-off** | `.deb`, a one-page README that starts with "plug in the widget, run `pdn-qso`", and a bench session with the person it is handed to | small |

Phases B, C and D are independent of each other once A exists; A comes first because everything else sits on it.

## 6. Open choices for Tom

- The name (`pdn-qso` is a placeholder).
- Whether the Flex device should expose power in the UI (convenient for perf ladders; easy to misuse on a shared rig).
- Whether Monitor mode should also write the daemon's frame-log format, so captures from the tool can be scored with the existing tooling.
- Whether the ARQ should be allowed to fall back to a more robust MS110D waveform automatically when retries pile up (the SETHW switch exists; the policy is a decision).
