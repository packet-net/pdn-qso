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

**Phase A2, the screen and the radios.** Monitor works end to end over a real device: the four transports are wired, the station comes up from a config, and every frame heard is on the screen and in the frame log.

- `PdnQso.Link` - the AX.25 link frame codec, the hermetic two-station audio rig (noise at a stated SNR, delay, dropouts), the station core (one device, one modem, a DCD-respecting transmit queue), the frame-log writer in the daemon's own SQLite schema, and the device layer: ALSA with CM108 or serial PTT, a FlexRadio over DAX with `rfpower` set and the forward-power meter read back, an UberSDR receiver, and a named-pipe pair for two copies of this tool on one machine.
- `PdnQso` - the real screen: a status bar, the always-on Monitor pane with a text/hex toggle, an activity pane on the F-keys, a log pane, the settings dialog and a first-run wizard that lists this machine's sound cards and finds a FlexRadio on the network.

Still to come: the chat ARQ (B), the fountain code and file transfer (C), and the perf numbers (D) - the three activities are placeholders that say so. [docs/plan.md](docs/plan.md) has the why, [docs/design.md](docs/design.md) the how.

## Using it

The first run, with no config, asks four questions: the radio, your callsign, the mode and where in the audio passband to put it. After that everything is in the settings dialog on F5, kept in `~/.config/pdn-qso/config.json`.

```
pdn-qso                                     start on the configured radio
pdn-qso --monitor-only                      listen and log, never transmit
pdn-qso --device flex:discover --mode qpsk2400 --callsign M0LTE-7
pdn-qso --config /etc/pdn-qso-test.json     a second instance with its own settings
```

`--device`, `--mode` and `--callsign` are for that session only and are never written back.

| Key | |
|---|---|
| F1 / F2 / F3 | Chat, File, Perf |
| F4 | Monitor, full screen |
| F5 | Settings |
| F6 | payload as text or hex |
| Ctrl+Q | quit |

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
