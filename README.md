# pdn-qso

A terminal tool for interactive two-way testing over the [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) modems. One radio, one correspondent, and a screen full of what actually happened: no AX.25 node in the way, no BBS, no routing. It is a test instrument with a friendly face.

Four things to do with it, in one full-screen keyboard-only UI that works over ssh and fits in 80x24:

| | What you see | What goes on air |
|---|---|---|
| **Monitor** (always on screen) | every frame heard, and every frame you send: time, callsigns, modem, SNR, carrier offset, quality, the payload as text or hex | nothing of its own |
| **Chat** | a keyboard-to-keyboard conversation with delivery ticks | short acknowledged messages |
| **File** | a transfer in either direction, with the receiver's have/need count | rateless fountain-coded blocks and small status frames |
| **Perf** | frames sent, heard and delivered, goodput, mean and worst SNR, round-trip time, time on air | a scripted stream of numbered frames, or a ping-pong |

Three devices: a **FlexRadio** 6000-series over the LAN (DAX audio, PTT and power), any **sound card** with a CM108 PTT widget, and a public **UberSDR** web receiver for listening only. Any mode pdn-soundmodem ships: the packet modes, the FreeDV datac modes, the MIL-STD-188-110D waveforms.

Everything it sends is an ordinary AX.25 UI frame inside the modem's own framing, so a monitor, a node or the daemon's frame log sees well-formed traffic and this tool coexists on a shared channel rather than jamming it.

## Start here

Plug in the CM108 widget, install the package for your architecture, and run it:

```
sudo apt install ./pdn-qso_<version>_<arch>.deb
pdn-qso
```

It is a program you run as yourself, not a service. The `.deb` is a self-contained build, so the target needs no .NET runtime.

The first run has no config, so it asks four questions and writes the answers to `~/.config/pdn-qso/config.json`:

1. **The radio.** It lists this machine's sound cards, looks on the network for a FlexRadio, and takes an UberSDR host if you would rather listen to somebody else's receiver.
2. **Your callsign**, with an SSID if you want one (`M0LTE-7`).
3. **The mode**, from everything pdn-soundmodem ships.
4. **Where in the audio passband to put it**, in Hz, and for a Flex or an UberSDR the RF frequency the audio centre is to land on.

Everything after that is the settings dialog on **F5**. Nothing is hidden in a file you have to hand-edit.

## The keys

| Key | |
|---|---|
| F1 / F2 / F3 | Chat, File, Perf |
| F4 | Monitor, full screen |
| F5 | Settings |
| F6 | payload as text or hex |
| Ctrl+Q | quit |

Each activity puts the cursor where you are about to type. In Chat that is the line at the bottom: type and press Enter, and the tick beside the line says `sending`, then `ok` with the number of tries and the round trip, or `failed` with the reason. In File it is the path box: type a path, press Enter, and it goes. A receiver is always running, so the far end never has to accept anything by hand; files land in `~/pdn-qso-received` unless the settings say otherwise. In Perf it is the Start button.

## The command line

```
pdn-qso                                     start on the configured radio
pdn-qso --monitor-only                      listen and log, never transmit
pdn-qso --device flex:discover --mode qpsk2400 --callsign M0LTE-7
pdn-qso --config /etc/pdn-qso-test.json     a second instance with its own settings
```

`--device`, `--mode` and `--callsign` are for that session only and are never written back.

## The three radios, and what each one needs

- **Sound card plus CM108 widget** (`plughw:1,0`): the card, and the PTT line. PTT is `none`, `cm108` (with the device node and the GPIO pin) or `serial` (with RTS or DTR); nothing can guess which one is wired, so it is four settings. The card runs at its own rate (48000 is the default and works for every mode) and the audio is resampled by a whole number to the mode's rate.
- **FlexRadio** (`flex:discover`, `flex:<ip>`, `flex:<ip>:<slice>`, or a trailing `@station` to coexist with a running SmartSDR): audio over DAX, PTT and power over the LAN. Power is set in watts and the forward-power meter is read back beside it, so the status bar says both what you asked for and what the radio delivered. Give it the RF frequency, not the dial: the dial follows from the audio centre and the sideband.
- **UberSDR** (`ubersdr:<instance>`): a public web receiver, so **receive only**. Use it for Monitor, or as the receiving half of a two-way test with a transmitter somewhere else. There is no PTT and no power, and the status bar says so rather than showing a lamp that means nothing.

## Two stations on one machine, no radio at all

The `pipe:` device is a pair of named pipes standing in for the air, so two copies of the program on one machine hear each other. It is not a channel: no noise, no propagation, samples arrive exactly as they were written. That makes it the right way to learn the screen and to prove two ends talk, and the wrong way to measure a modem.

In one terminal:

```
pdn-qso --config ~/a.json --device pipe:/tmp/qso-ab,/tmp/qso-ba,48000 --callsign M0LTE-1
```

In another, with the two paths the other way round:

```
pdn-qso --config ~/b.json --device pipe:/tmp/qso-ba,/tmp/qso-ab,48000 --callsign M0LTE-2
```

The rate at the end has to be a whole multiple of the mode's own rate; 48000 is a whole multiple of every mode this tool has. Press F1 on both, type a line on one, and it appears on the other with a tick beside it on the sender. F2 sends a file the same way, F3 measures the link.

## What the Perf numbers mean

- **sent / heard / delivered.** Sent is what this station put on air. Heard is what the far end decoded. Delivered is what it decoded and had not already seen, so heard minus delivered is duplicates.
- **frame errors.** The fraction of the stream that did not arrive. A frame either arrives with its CRC intact or does not arrive at all, so this is a frame error rate and not a bit error rate.
- **goodput.** Payload bytes per second of elapsed time, including the gaps and the turnarounds. It is the number to quote when comparing modes: the bit rate on the box is what the modem does, and this is what the link does. A ping-pong reports no goodput, because it is measuring turnaround rather than throughput.
- **snr mean / worst / last.** What the far end's demodulator made of the signal, as it reported it per frame. Modes that do not estimate SNR report `n/a`, and so does a noiseless pipe.
- **rtt mean / worst.** Round trip for the ping-pong: how long between putting a frame on air and hearing its answer, including both stations' TX delays and turnaround.
- **at.** When the run started, so a screenshot is a complete measurement: procedure, mode, centre, device, power and numbers are all on one screen.

**Export** appends the run as one row to `~/pdn-qso-perf.csv`, writing the header first if the file is new, and puts the text summary in the log pane. The row is RFC 4180 quoted, so a device string with commas in it stays one column.

## Where things are kept

| | |
|---|---|
| Settings | `~/.config/pdn-qso/config.json` |
| Frame log | `~/.local/share/pdn-qso/frames.db`, the daemon's own SQLite schema, so the existing tooling scores it |
| Received files | `~/pdn-qso-received` |
| Perf CSV | `~/pdn-qso-perf.csv` |

## When it does not work

- **No audio device.** The wizard lists the cards it can see; if yours is not there, `arecord -l` will say whether the system can see it either. A card in use by something else will not open twice.
- **PTT does not key.** The CM108 GPIO pin is not standardised across widgets: 3 is the common one, but not universal. Check the device node is the widget's own `hidraw`, that you can write to it, and that the pin matches the widget. A serial line is RTS or DTR and never both. The Monitor pane shows your own transmissions, so if the pane says you transmitted and the radio did not key, the fault is on the PTT line and not in the modem.
- **Nothing is heard.** Almost always the dial, the centre or the mode. Both ends have to be on the same mode, and the far station's audio has to land inside your receiver's passband: check the RF frequency, the sideband, and that the audio centre is somewhere your filter passes. The waterfall in pdn-soundmodem's own daemon is the quickest way to see whether the signal is arriving at all and where.
- **Heard but never decoded.** Levels. Too little audio and the demodulator has nothing; too much and it clips. Set the input gain so a strong signal peaks below full scale, not at it.
- **A transfer finishes late.** The receiver reports on a status interval, so a small file on a clean link can be on disc for one interval before the sender learns it arrived and stops. The file is intact; the sender is being patient.

## Building from source

```
dotnet build -c Release
tests/PdnQso.Tests/bin/Release/net10.0/PdnQso.Tests
```

Needs the .NET 10 SDK. The tests are hermetic: two stations joined through a simulated channel, no radio and no sound card involved.

[docs/plan.md](docs/plan.md) has the why, [docs/design.md](docs/design.md) the how.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE).

It links `M0LTE.Flex` (AGPL-3.0) and `pdn-soundmodem` (GPL-3.0-or-later), which GPLv3 section 13 expressly permits to be combined with AGPL-3.0 code.
