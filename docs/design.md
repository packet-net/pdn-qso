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

## 6d. Time in the tests

**No test reads the wall clock.** Two of them decided things by it and both failed on a loaded
CI runner in one day; the fix is structural rather than a larger number.

- **`VirtualClock`** is a `TimeProvider` with its own timer queue. It starts at a fixed instant
  and moves only when something moves it. Every `Station`, `ChatSession`, `FileSender`,
  `FileReceiver` and `PerfRun` in a test is given one.
- **`VirtualTime`** drives it. `WaitForAsync(fact)` waits for something to become true with **no
  deadline at all**: a deadline is a wall-clock measurement, and a test that hangs is a finding
  the runner reports honestly. `RunAsync`/`UntilAsync` let the clock move so protocol timeouts
  fire, on one rule: let everything runnable run, and move the clock on only when nothing is
  busy and nothing has moved since the last look.
- **Anything the clock must not be run past has to say so from the instant it takes the work
  on**, not from when its own pump wakes up: `AudioLink.Carrying` (a burst in the air),
  `ChatSession.Sending` (an answer owed, counted where the frame is posted),
  `FileReceiver.Busy`. A flag raised late leaves a gap, and the gap is where a timeout fires
  against an answer that was already on its way.
- **A rig may charge for air time** by subscribing `AudioLink.Carried` to its clock, which moves
  it by each burst's own sample count over the link's rate. The file transfer tests do, because
  a sender that transmits for free can pour symbols for ever without a patience measured in
  seconds ever coming due. Their intervals are the shipped defaults' shape, an order of
  magnitude quicker, because a block takes about a second at 1200 baud and an interval shorter
  than that is not a test of anything.

**The rig itself must not let the machine vote either.** Two things were found doing this and
both are properties of the rig rather than of any test:

- `AudioLink` drew its noise from one `Random` shared by both ends and consumed in whatever
  order the two stations happened to transmit. The lossy transfer test therefore saw a
  different link every run. Each burst now draws noise seeded by which end sent it and how many
  that end has sent, so the tenth frame from a station always meets the same noise however the
  two ends interleave. Counted per direction, so one end's traffic cannot shift the other's.
- A receiving run is started by a call and is not listening until it has subscribed. On the air
  the far end is never started by the same keystroke; in a test it is, and the first frame of
  the run goes to nobody. `PerfRun.Listening` says when it would really hear something.

**What was tried and does not work.** An exact quiescence signal - each party publishing the
clock time it has caught up with, and counting as busy until it has - deadlocks. An air-time
advance from the transmitting thread can move the clock between a party observing the time and
parking on its next timer, and that party then waits for a clock that is waiting for it. The
margin above (sixteen quiet rounds) is a margin and is described as one.

Driving this found five defects in the library that the wall clock had been covering: a ping
timeout armed before the frame it timed had been sent, two timeouts whose timers were left
running after the answer arrived, a receiver that sat on a queued frame until its next poll tick
instead of answering when it arrived, no way for a caller to know a receiving run had started,
and (filed as #11 rather than fixed) a finished receiver whose Done frames are all lost going
quiet while the sender spends its whole patience on it.

### 6e. An answer in hand beats its own stopwatch

Issue #12 recorded a frame that occasionally went missing on a noiseless link and put it down to
the modem. It was not the modem. Driven directly, with nothing of this repo in the path, the
modems decoded five thousand frames out of five thousand under a dozen CPU burners; the fault was
here, and it was not a lost frame at all. It was a lost **answer**.

The shape of it, and the rule that comes out of it:

- **A timeout is only a timeout when the answer is not already in hand.** `Task.WaitAsync(timeout)`
  is a race between the answer and the timer, and a `TaskCompletionSource` that runs its
  continuations asynchronously - which every one of ours does, because answering from inside the
  far station's transmit would re-enter it - hands the answer's continuation to the thread pool
  and lets the timer fire while it is still queued. On the wall clock that window is nanoseconds
  wide. On a clock a test drives it is however long the machine takes to give the task a thread,
  and the timer fires the instant the settle loop decides to move time on. `ChatSession` was
  retransmitting lines the far end had acknowledged, and `PerfRun`'s frame wait was reporting an
  answer it had decoded as no answer at all. Both now ask what arrived rather than who won.
- **Measure where it happened, not where it was noticed.** A round trip read when the waiting
  task next gets a thread includes the machine's queue, which on a busy box is the larger of the
  two numbers. The acknowledgement's arrival is stamped in the receive handler and the
  transmission's end in the transmit pump, and the figure is the difference between those.
- **The party waiting for an answer needs a flag too, not just the party that owes one.** The
  rule above has always been written from the responder's side: an answer is owed from the
  instant the frame is taken in. The asking side has the same gap and had nothing to say about
  it. What a probe is waiting for arrives on the far station's transmitting thread, and the
  asking run resumes whenever the machine next gives it a thread; move the clock through that
  and the probe's own patience fires against a reply the station has already decoded.
  `PerfRun.Answered` is the flag, in the shape of `AudioLink.Carrying`: raised where the frame
  is decoded, put down where the run takes it up.
- **A flag that says "work in hand" has to stay up for the whole of the work.** `FileReceiver`
  raised it while frames sat in its inbox and while it was transmitting, and put it down in
  between - which is where the last symbol is peeled, the CRC checked and the file written. The
  clock could be moved through that gap, and the sender then poured symbols at a receiver that
  was about to say Done. It now covers the turn, and is put down only for the waits: the poll
  and the Done linger, which are over when the clock says so and would otherwise be waiting for
  a clock that was waiting for them.

The general form of the last one is already the rule at the top of this section. The first two
are the same rule from the other side: **a decision must not depend on which continuation the
machine ran first either.** Both are real on the air as well - a needless retransmission costs air
time, and a round-trip figure that is really a thread-pool measurement is not a measurement - so
the fix belongs in the library rather than in a bigger margin in the rig.

## 7. Phases and agents

A (skeleton + Monitor + devices + settings) first; then B (chat ARQ), C (fountain + file), D (perf) in parallel; E (packaging, README, hand-off). Each phase is a PR with its own tests; the skeleton phase also brings `ci.yml` and `release.yml` (copied in shape from pdn-soundmodem, self-hosted runner labels, release notes from PR titles via `scripts/release-notes.py`).

**What shipped.** All of it, on 2026-08-21, in that order, with a wiring pass between D and E that replaced the placeholder activities with the real ones. Three things the hand-run of two copies over a `pipe:` pair found, which no hermetic test had:

- **Focus.** Switching activity left the input unfocused, so an operator pressed F1, typed their first line and nothing happened at all. The `Shown` seam above, and `CanFocus` on the containers.
- **The rate bridge.** `DecimatingAudioInput` dropped the part-frame tail of every read instead of carrying it into the next one, so a device paced to wall clock (which returns a multiple of the decimation factor only by accident) slipped one to three samples per read and nothing decoded. That is the path every sound card at 48 kHz uses, and the 1:1 pipe test never went near it. Fixed, with a test that pins the ragged-read case against the tidy one.
- **The CSV.** A pipe device is spelled `pipe:<in>,<out>,<rate>` and put two unquoted commas in the device column, making every row three fields wider than its header. RFC 4180 quoting on the text fields.

Left open, deliberately: a receiver's Done that collides with the sender's own transmission is not heard, so a clean transfer can cost one status interval more than it should. The recovery works (the sender re-offers, the receiver Dones again), so this is a tuning question with a measurement behind it rather than a hand-off fix.
