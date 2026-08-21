# pdn-qso - design

Binding on layout, protocol and conventions. Written 2026-08-21 from [plan.md](plan.md) and Tom's decisions: name `pdn-qso`; power control on every transmit-capable device (Flex through `rfpower` with the radio's watts read back, CM108 and any ALSA card through the card's playback mixer volume); Monitor is a pane that is always on screen, not a mode; Monitor writes the daemon's frame-log format; the ARQ may step the MS110D waveform down or up; licence AGPL-3.0-or-later; built with sub-agents.

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

## 4a. Power

One `IPowerControl` in the station, implemented per device: **Flex** sets `rfpower` through `M0LTE.Flex` and reads the forward-power meter back, so the UI shows "set 10 W, reading 9.6 W"; **ALSA/CM108** sets the playback mixer volume of the selected card (the simple mixer API of libasound through a small P/Invoke, or `amixer` if that proves simpler - either way the control name and card are discovered, not assumed, and the UI shows the percentage and dB the mixer reports); **UberSDR** has none. Perf's ladder runs step power through this interface. The station ceiling on Tom's Flex (15 W) is honoured by refusing settings above what the radio reports as its maximum, never by clamping silently.

## 5. The station core

`Station` owns one device (input + output + PTT, or input only for UberSDR), one `IModem`, the busy detector, the transmit queue (one frame at a time, DCD-respecting, TX delay from settings, ident per the library's `StationIdentifier` rules), and the frame-log writer. Everything above it talks in link frames; everything below it is the library. Receive is always on; the modem is autobaud where the waveform allows.

## 6. The UI

Terminal.Gui 2.4.x. Layout: a status bar (device, mode, centre, power, PTT and DCD lamps, last SNR, correspondent); the **Monitor pane, always on screen** (every frame heard, scrolling, with callsigns, modem, SNR, offset and quality - full height when no activity is selected, a lower pane otherwise); a main pane for the active activity (Chat, File, Perf); F-keys for switching activity and for the settings dialog. Settings (all in the dialog, persisted to `~/.config/pdn-qso/config.json`): device string, callsign, modem mode, audio centre, TX delay ms, audio in/out gain, power (watts on Flex with the read-back beside it; mixer percentage on a sound card), ident interval and callsign, ARQ timeouts and retries, fountain c/delta, frame-log path. First run, with no config: a wizard that lists ALSA cards, discovers Flex radios, or takes an UberSDR host, then the callsign, then the mode.

### 6a. What phase A2 added to this list

Built and found missing while building it. Recorded here because this document is binding and a
settings list that does not start a station is not much of a specification.

- **RF frequency.** The list above has an audio centre and no dial. A Flex is told where to tune
  and an UberSDR cannot guess, so there is one more setting: the RF frequency the modem's audio
  centre is to land on. The dial follows from it and the sideband
  (`PdnQso.Link.Devices.DialFrequency`), and a sound card ignores it because the rig's own VFO
  decides. It is also what the frame log's `rf_hz` column gets.
- **Which PTT line, and where.** "A sound card with a CM108 PTT widget" is four settings once it
  is real: none / cm108 / serial, the device node, the CM108 GPIO pin, and RTS or DTR. The
  library keys all of them; nothing here can guess which one is wired.
- **Capture rate.** The modes run at 12 or 48 kHz and plenty of cards will not open 12 kHz at all,
  so the card runs at its own rate and the adapter resamples by a whole number, exactly as
  pdn-soundmodem's daemon does. 48000 works for every mode this tool has, and is the default.
- **Sideband, DAX channel, antenna, UberSDR mode and password**, for the same reason: a device
  string names a radio and not how to talk to it.

### 6b. The activity seam

The main window owns the layout, the Monitor pane, the status bar and the station. An activity
owns one view and the conversation it is having over the station it was handed:

```csharp
public interface IActivityView
{
    string Title { get; }                 // the tab name: Chat, File, Perf
    Terminal.Gui.ViewBase.View View { get; }
    void Attach(IStation station);        // called again on every station restart
}
```

Two things it obliges an activity to get right. `Attach` is called more than once - changing the
device, the mode or the audio centre restarts the station - so an activity has to drop whatever
it held from the previous one rather than keep talking to a station that has gone. And a
station's frame events arrive on the capture thread, so anything touching a view goes through
`IApplication.Invoke` first.

The Monitor pane shows every frame **heard and sent**. `IStation.FrameTransmitted` raises the
decoded link frame and the raw bytes after the transmitter has dropped, and the pane renders it
with the line formatter's `outgoing` flag, so an operator on a link where nothing is coming back
can still see their own traffic go out. (This was the A2 limitation; it is lifted.)

An activity is also told when it comes on screen, which is not the same occasion as `Attach`:

```csharp
void Shown();     // put the cursor where the operator is about to type
```

`Attach` is about the station and fires on a restart; `Shown` is about the screen and fires on
every F-key. It has to be separate because a view that is not visible cannot take focus, so an
activity that focuses its own input as it is built focuses nothing. The same applies one level
up: in Terminal.Gui a view whose container cannot be focused is unreachable from the keyboard
however focusable it is itself, so the panes and each activity's root view set `CanFocus`.

## 6c. Packaging and the upgrade

The release attaches one `.deb` per architecture under a name with **no version in it**:
`pdn-qso_amd64.deb`, `pdn-qso_arm64.deb`, `pdn-qso_armhf.deb`. The version is in the package's
own control data, where dpkg reads it, and in the release title. That makes
`releases/latest/download/pdn-qso_<arch>.deb` a URL that always points at the current release
and never has to be rewritten, which is what a README, a wiki page, and `pdn-qso --upgrade` can
all rely on.

`--upgrade` is the whole update story: there is no apt repository to add, no key to trust and
no background check. It

1. maps this **process's** architecture (not `dpkg --print-architecture`, which would name the
   kernel's) to a package name;
2. asks GitHub for `latest/download/<name>` and reads the tag out of the **first** redirect.
   GitHub answers with a 302 to `releases/download/<tag>/<name>` and only then with a second
   redirect to a signed URL on another host that carries no tag at all, so the first hop is
   where the version is, and the second hop's URL is the one to download: the bytes fetched are
   the bytes of the release just identified even if another is published in between. No API
   call, no token, no rate limit. GitHub's "latest" is the latest release that is not marked as
   a prerelease, and `release.yml` marks any version with a hyphen in it (`0.3.0-rc1`) as one,
   so an rc is published, is downloadable by its own URL, and is never what `--upgrade` offers;
3. stops if the versions match, and stops if the running copy is ahead (a build from source
   says `1.0.0`, because that is what the SDK stamps when no tag named it);
4. downloads the package and the release's own `SHA256SUMS` and refuses to install anything
   that is not in that file or does not match it;
5. installs with `apt-get install --yes --reinstall`, through `sudo` when not root, because the
   package depends on `libasound2 | libasound2t64` and an alternation is exactly what dpkg
   cannot resolve on its own. With neither root nor sudo it leaves the download in place, prints
   the command, and exits 1.

Everything above except the network and the process call is in `ReleaseAsset`, which is pure
and tested; `SelfUpgrade` is the thin shell around it.

## 7. Phases and agents

A (skeleton + Monitor + devices + settings) first; then B (chat ARQ), C (fountain + file), D (perf) in parallel; E (packaging, README, hand-off). Each phase is a PR with its own tests; the skeleton phase also brings `ci.yml` and `release.yml` (copied in shape from pdn-soundmodem, self-hosted runner labels, release notes from PR titles via `scripts/release-notes.py`).

**What shipped.** All of it, on 2026-08-21, in that order, with a wiring pass between D and E that replaced the placeholder activities with the real ones. Three things the hand-run of two copies over a `pipe:` pair found, which no hermetic test had:

- **Focus.** Switching activity left the input unfocused, so an operator pressed F1, typed their first line and nothing happened at all. The `Shown` seam above, and `CanFocus` on the containers.
- **The rate bridge.** `DecimatingAudioInput` dropped the part-frame tail of every read instead of carrying it into the next one, so a device paced to wall clock (which returns a multiple of the decimation factor only by accident) slipped one to three samples per read and nothing decoded. That is the path every sound card at 48 kHz uses, and the 1:1 pipe test never went near it. Fixed, with a test that pins the ragged-read case against the tidy one.
- **The CSV.** A pipe device is spelled `pipe:<in>,<out>,<rate>` and put two unquoted commas in the device column, making every row three fields wider than its header. RFC 4180 quoting on the text fields.

Left open, deliberately: a receiver's Done that collides with the sender's own transmission is not heard, so a clean transfer can cost one status interval more than it should. The recovery works (the sender re-offers, the receiver Dones again), so this is a tuning question with a measurement behind it rather than a hand-off fix.
