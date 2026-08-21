# pdn-qso - design

Binding on layout, protocol and conventions. Written 2026-08-21 from [plan.md](plan.md) and Tom's decisions: name `pdn-qso`; Flex power exposed in the UI; Monitor writes the daemon's frame-log format; the ARQ may step the MS110D waveform down or up; licence AGPL-3.0-or-later; built with sub-agents.

## 1. Projects

- **`src/PdnQso.Link`** (class library, AGPL-3.0-or-later, net10.0): everything that is not a screen. Depends on the `pdn-soundmodem` NuGet package (0.39.0 or later) for `IModem`, `ModemCatalog`, `ModemOptions`, `FrameQuality`, `IHardwareControllable`. Contains: the link frame codec, the chat ARQ, the LT fountain coder and the file transfer protocol, the perf stream and ping-pong, the frame-log writer, the station core (one modem, one device, DCD-respecting transmit queue, ident), and the device factory.
- **`src/PdnQso`** (console app): Terminal.Gui 2.4.x UI over `PdnQso.Link`. First-run wizard, settings dialog, one view per mode, status bar, log pane. Published self-contained for linux-x64 / linux-arm64 / linux-arm.
- **`tests/PdnQso.Tests`**: xunit v3 + AwesomeAssertions. Hermetic: two `Station`s joined by an in-process `AudioLink` (a pair of sample queues with optional white noise at a stated SNR in a 3 kHz bandwidth, optional fractional delay, optional dropout windows) so every protocol claim is pinned without a radio. The pdn-soundmodem test project's Watterson rig is not in the package; this repo's own `AudioLink` is simpler on purpose.

## 2. The library API this sits on (read, do not copy)

From `/home/tf/pdn-soundmodem/src/Packet.SoundModem/`:
- `Modems/IModem.cs`, `Modems/ModemCatalog.cs`, `Modems/ModemOptions` (centre frequency, plain-IL2P acceptance), `Modems/IHardwareControllable.cs` (MS110D's `TrySetHardware` - waveform 0-8/13 plus interleaver; the ARQ's step-down/up lever), `FrameQuality` (SNR, carrier offset, erased bytes, chased bits, monitor-only flag).
- `Audio/AlsaPcm.cs`, `Channel/AlsaAudioInput.cs`, `Channel/AlsaAudioOutput.cs`, `Channel/Cm108Ptt.cs`, `Channel/SerialPtt.cs`, `IPttControl`, `Channel/SoundModemChannel.cs`, `Channel/BurstSnrMonitor.cs`, `Modems/EnergyBusyDetector.cs`, `Modems/PacketDcd.cs`.
- `FlexRadio/FlexDevice.cs` (`FlexDevice`, `FlexRuntime`, `FlexTuning`) and `M0LTE.Flex` for power (`rfpower`, the 15 W station ceiling applies on Tom's rig; the UI shows watts read back from the radio, never only the setting).
- `UberSdr/UberSdrDevice.cs`, `UberSdr/UberSdrAudioInput.cs` (receive only; the station refuses to transmit on it).
- `Ident/StationIdentifier.cs`, `Ident/MorseGenerator.cs`.
- From the daemon (reference only, reimplement): `src/Packet.SoundModem.Daemon/FrameLog.cs` (the frame-log line format Monitor must write, field for field, so `sm-ota` and the survey tooling can read it), `PipeAudio.cs` (the `pipe:` device: two named pipes at a sample rate), and the device-string parsing in `Program.cs` (`alsa:`/`default`, `flex:<radio>[:slice][@station]`, `ubersdr:<instance>`, `pipe:<in>,<out>,<rate>`).

## 3. The link protocol

Every frame the tool sends is an **AX.25 UI frame** (destination `QSO`, source = the station callsign with SSID, PID 0xF0) so that monitors, nodes and the daemon's frame log see well-formed traffic. The information field starts with a two-byte header:

```
byte 0   type: 0x01 CHAT, 0x02 CHAT-ACK, 0x10 FILE-OFFER, 0x11 FILE-SYMBOL, 0x12 FILE-STATUS,
               0x13 FILE-DONE, 0x20 PERF-STREAM, 0x21 PERF-PING, 0x22 PERF-PONG, 0x7F HELLO
byte 1   session id (random per conversation / transfer / run), then type-specific fields
```

- **Chat**: `seq(1)` + UTF-8 text. Stop-and-wait: send, wait `ackTimeout` (derived from the mode's frame time plus a fixed margin), retry up to `maxRetries` with a backoff that waits for DCD clear plus a random slot. `CHAT-ACK` carries the seq. The UI shows sent / delivered / failed per line. **Waveform step-down/up**: when the modem is MS110D and two consecutive retries fail, the station asks the modem to step to the next more robust waveform (8 -> 7 -> 6 -> 5 -> 4 -> 2) and tells the correspondent in the next frame's header flag; after `stepUpAfter` consecutive clean deliveries it steps back up one. The correspondent's receiver is autobaud, so nothing is negotiated.
- **File**: `FILE-OFFER` (file id, name, size, block size K, CRC-32 of the file); `FILE-SYMBOL` (symbol index, then the LT-coded payload; the receiver regenerates the degree and neighbour set from the index with the agreed PRNG seed); `FILE-STATUS` from the receiver every `statusInterval` (decoded n of K); `FILE-DONE` when decoded and the CRC matches. The sender emits the K source symbols first (systematic), then repair symbols until DONE or a patience limit. Block size is the mode's payload capacity minus the header.
- **Perf**: `PERF-STREAM` frames of a fixed size with a running sequence and a timestamp; the receiver counts and reports. `PERF-PING`/`PONG` through the chat ARQ for round-trip time. The numbers pane shows sent, heard, delivered, frame error rate, goodput (payload bytes per second of air time, air time from the modem's own burst length), mean / worst / last SNR, RTT mean / worst, mode, centre, device; `Export` writes a CSV row and a text summary.
- **HELLO**: on start and on demand, so the other side's callsign appears in the status bar.

## 4. The fountain code

LT codes (Luby 2002) with the robust soliton distribution (MacKay's exposition, parameters c and delta exposed as settings with sane defaults), a systematic first pass, XOR combining, and a peeling decoder that keeps undecodable symbols until a new one releases them. `PdnQso.Link.Fountain` is a pure unit with no I/O: tests pin decode completion against the predicted overhead at several K, and that a corrupted symbol (the CRC catches it at the modem layer, so it is simply absent) only delays completion.

## 5. The station core

`Station` owns one device (input + output + PTT, or input only for UberSDR), one `IModem`, the busy detector, the transmit queue (one frame at a time, DCD-respecting, TX delay from settings, ident per the library's `StationIdentifier` rules), and the frame-log writer. Everything above it talks in link frames; everything below it is the library. Receive is always on; the modem is autobaud where the waveform allows.

## 6. The UI

Terminal.Gui 2.4.x. Layout: a status bar (device, mode, centre, PTT and DCD lamps, last SNR, correspondent); a main pane that is the current mode's view; a log pane; F-keys for mode switching and the settings dialog. Settings (all in the dialog, persisted to `~/.config/pdn-qso/config.json`): device string, callsign, modem mode, audio centre, TX delay ms, audio in/out gain, Flex power (watts, with the radio's read-back beside it), ident interval and callsign, ARQ timeouts and retries, fountain c/delta, frame-log path. First run, with no config: a wizard that lists ALSA cards, discovers Flex radios, or takes an UberSDR host, then the callsign, then the mode.

## 7. Phases and agents

A (skeleton + Monitor + devices + settings) first; then B (chat ARQ), C (fountain + file), D (perf) in parallel; E (packaging, README, hand-off). Each phase is a PR with its own tests; the skeleton phase also brings `ci.yml` and `release.yml` (copied in shape from pdn-soundmodem, self-hosted runner labels, release notes from PR titles via `scripts/release-notes.py`).
