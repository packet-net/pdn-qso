# pdn-qso

A terminal tool for interactive two-way testing over the [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) modems. One radio, one correspondent, and a screen full of what actually happened: no AX.25 node in the way, no BBS, no routing. It is a test instrument with a friendly face.

Four things to do with it, in one full-screen keyboard-only UI that works over ssh and fits in 80x24:

| | What you see | What goes on air |
|---|---|---|
| **Monitor** (always on screen) | every frame heard: time, callsigns, modem, SNR, carrier offset, quality, the payload as text or hex | nothing |
| **Chat** | a keyboard-to-keyboard conversation with delivery ticks | short acknowledged messages |
| **File** | a transfer in either direction, with the receiver's have/need count | rateless fountain-coded blocks and small status frames |
| **Perf** | frames sent, heard and delivered, goodput, mean and worst SNR, round-trip time, time on air | a scripted stream of numbered frames, or a ping-pong |

Three devices: a **FlexRadio** 6000-series over the LAN (DAX audio, PTT and power), any **sound card** with a CM108 PTT widget, and a public **UberSDR** web receiver for listening only. Any mode pdn-soundmodem ships: the packet modes, the FreeDV datac modes, the MIL-STD-188-110D waveforms.

Everything it sends is an ordinary AX.25 UI frame inside the modem's own framing, so a monitor, a node or the daemon's frame log sees well-formed traffic and this tool coexists on a shared channel rather than jamming it.

## Status

**Phase A, the foundations.** What exists today is the library every later phase sits on, and a skeleton UI that opens a window and quits on Ctrl+Q.

- `PdnQso.Link` - the AX.25 link frame codec, the hermetic two-station audio rig (noise at a stated SNR, delay, dropouts), the station core (one device, one modem, a DCD-respecting transmit queue), the frame-log writer in the daemon's own SQLite schema, the power-control interface, and the device-string parser for `alsa:` / `flex:` / `ubersdr:` / `pipe:`.
- `PdnQso` - a Terminal.Gui window and nothing else yet.

Still to come: the settings screen and first-run wizard and the real device implementations (A2), the chat ARQ (B), the fountain code and file transfer (C), and the perf numbers (D). [docs/plan.md](docs/plan.md) has the why, [docs/design.md](docs/design.md) the how.

## Building and running

```
dotnet build -c Release
tests/PdnQso.Tests/bin/Release/net10.0/PdnQso.Tests
```

Needs the .NET 10 SDK. The tests are hermetic: two stations joined through a simulated channel, no radio and no sound card involved.

Releases are cut as `.deb` packages for amd64, arm64 and armhf, each a self-contained build with no .NET runtime dependency on the target:

```
sudo apt install ./pdn-qso_<version>_<arch>.deb
pdn-qso
```

It is a program you run as yourself, not a service. Settings will live in `~/.config/pdn-qso/config.json`.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE).

It links `M0LTE.Flex` (AGPL-3.0) and `pdn-soundmodem` (GPL-3.0-or-later), which GPLv3 section 13 expressly permits to be combined with AGPL-3.0 code.
