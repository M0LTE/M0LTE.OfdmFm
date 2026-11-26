# The live station: the design

Status: a design, approved by Tom on 2026-11-26 (see Decided); steps 1 to 4 of K are built (the clock, timers and transmit handshake; streaming detection; reception; busy and channel access, as described under "Step 3 as built" and "Step 4 as built" below), the rest is not. Section F carries a correction from step 4, marked in place. Every name marked "proposed" is a suggestion, not an existing API. It designs what this library lacks: something that takes received audio as it arrives, finds and decodes bursts, decides when the channel is free, decides what to send and when, and talks to a host program. The protocol is [adaptation-protocol.md](adaptation-protocol.md). The operations and events follow the [host interface](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ofdm-fm/host-interface.md) in packet-net/pdn-soundmodem.

## A. One clock: the receive stream's sample count

The station never reads a wall clock. Time is samples received so far at 48 kHz, and every timer is measured on it: the first slot, the random delay, how long an asked-for report may wait, the 30 s and ten-minute rules inside `LinkAdaptation` (it takes `now` in seconds; the station passes samples over 48000), and the identification interval.

A transmission is a request: "these samples, starting at receive-sample N or as soon after as possible". The audio layer reports the receive-sample index at which it started and ended. Receive audio keeps flowing, muted or not, while the radio transmits, so the count never stops. If the audio layer drops samples it says how many (proposed `Skip(count)`).

Tests push blocks of samples and are deterministic. Random numbers come from a seeded source the caller supplies.

## B. Shape of the API

A proposed `Station`, one per radio channel. Settings: callsign, station number, random source; nothing else.

- `Push(ReadOnlySpan<float>)`: received audio, any block size, returns quickly (D).
- `Skip(int)`, `TransmitStarted(id, sampleIndex)`, `TransmitEnded(id, sampleIndex)`, `SetExternalBusy(bool)`: from the audio layer (E).
- `Send(datagram, name, tag, reportWanted, priority)`, `Cancel(tag)`, `Cancel(name)`, `Snapshot()`: from the host, as the host interface defines them. Send is refused for a datagram larger than one burst to that station now carries, or a full queue.
- Out to the audio layer: events `TransmitRequest` (id, samples, earliest receive-sample) and `WithdrawRequest` (id), for a request not yet keyed when the channel turns busy.
- Out to the host, all raised by the library: `Transmitted` (on `TransmitEnded`), `Dropped`, `Received`, `PathReport`, `BurstNotDecoded`, `LinkState`, `ChannelBusy`, `Congestion`, `ModemSent`. Added, proposed: `IdentificationDue` (H).

## C. Streaming burst detection

`BurstFinder.Find` correlates a whole recording by FFT, takes its threshold from the median of the whole recording's correlation, and keeps peaks that are the largest within a symbol either side. The streaming form, for each of the three openings:

1. **Correlation in overlapping blocks.** The audio is transformed once per block and shared. Each opening has a precomputed reference spectrum (1, 3 or 6 symbols: 2112, 6336, 12672 samples). The energy under the reference comes from a running sum of squares, started afresh in every block so that it cannot drift, so the normalised value is the one `Find` computes. Built in step 2 as `StreamingBurstDetector` (internal): blocks of 32768 samples overlapping by 12671, so 20097 new values per opening per block; one forward transform and two inverse ones per block (two openings share an inverse, one in its real part and one in its imaginary part).
2. **Threshold:** the larger of 0.1 and 7 times a running median of the correlation (every 16th value, as `Find` does) over the last 30 s, kept as a histogram. The estimate pauses over each burst whose header has been read, from its first sample to its last (by sample index, as step 3 does it), and during the station's own transmissions, so a strong burst does not lift it. For the first 5 s only 0.1 applies, and the station does not transmit.
3. **The same peak rule, one symbol late.** The offline rule is not quite "the largest within a symbol either side": it keeps strict local maxima over the threshold, then works from the highest down and removes any peak closer than a symbol to one it has kept. So a lower peak beside a higher one that was itself removed stays. The streaming form applies that rule to a short list of pending peaks and settles a peak once every higher peak within a symbol is settled and a symbol past it has been seen. A candidate comes out between 0.24 and 0.46 s after its reference ends in the vectors, most of it the block wait.
4. **Header read** (step 3 reads them one at a time, in the order the detector gave the candidates, each once the opening's samples are in the ring as of the last frame's end) when the opening's samples are in: 4224 samples on the full band, up to 19008 (0.4 s) on the 1.5 kHz opening. `HeaderReader.TryRead` gates the candidate. Candidates of different openings reading the same header within a symbol are one burst. `PayloadReceiver.SamplesNeeded(header)` then gives the burst's length.

Buffering: a ring of about 16 s. The longest burst is 261 symbols, 11.5 s, plus lead-in and a pad either side for the resampler. A complete burst is copied to a pooled buffer for its job, so the ring keeps moving.

Cost per second of audio, in existing pieces: one forward and two inverse transforms per block, a running sum, the histogram, and a header read per candidate. Measured in step 2: about 11 ms of CPU per second of audio for all three openings (an i7-13700, Release), 70 percent of it in the transforms. False candidates are the risk: on white noise the full-band opening gives about 300 an hour, on noise rising as an FM receiver's does none in 20 minutes, and for the first 5 s, on the floor alone, noise in the band gives many. A station is deaf to a second burst for up to 0.4 s after one starts.

## D. Decoding without blocking the audio

`Push` does correlation and peak rule only. Header reads and payload receives are jobs.

- The station queues jobs. The caller runs them on threads of its choosing: `HasWork`, `RunPending()`, `RunOne()` (built as proposed, with a `WorkAvailable` event so that a worker can sleep on a signal). Each thread running a job has its own `HeaderReader` and `TrackedReceiver` (large buffers, not thread-safe), which the station makes on first use. An inline mode, chosen at construction, runs jobs inside `Push` for tests.
- A burst arriving mid-decode is queued first in, first out, its audio already copied. More than four queued bursts: the oldest is dropped. Built: nothing is raised for it (see "Step 3 as built").
- Results are applied in order of burst start, whichever job finishes first, and `LinkAdaptation` is given the burst's end time on the sample clock, not the verdict's. Decode speed never changes what the rules see (what holds a result back until it can be applied is under "Step 3 as built").
- The channel is busy from the confirmed candidate, so a long decode does not make it look free.

A late verdict: the protocol says a failed burst is reported at once. The station sends the lone report as soon as the verdict is in and the channel has been clear 50 ms, even if the first slot passed; the prototype did the same, and nothing expires it. A newer verdict for the same station replaces it. A "drop" or "asked" report is cancelled if a data burst to that station, carrying the recommendation in its prelude, goes first. A "failed" report always goes alone, which `BurstNotDecoded` relies on (G).

## E. Busy detection

The prototype's method (the receiver's noise going quiet) fails at about -120 dBm. Busy is the union of:

- **A confirmed candidate.** If its header reads, busy to the burst's end from `SamplesNeeded`. If not, busy for the opening plus one second, then re-judged: a real burst may go on; a false candidate costs one second.
- **An unknown version.** The protocol says busy while signal is heard. Without an external indication: one second at a time while candidates keep arriving. An unknown type under a known version still carries its length.
- **The station's own transmission**, from granting a request to `TransmitEnded` plus a short tail.
- **An external indication** from the audio layer if it has one (squelch, signal strength): busy while set, and 100 ms after.

Each ends at the burst's last sample, at `TransmitEnded`, or when the indication drops. `ChannelBusy` is raised on each change. Candidates starting from a pad before the station's own transmission to 0.25 s after it are ignored: the audio is muted and a normalised correlation finds bursts in nothing.

## F. Channel access

Terms are the host interface's: the physical state machine, T101 (priority window), T102 (slot delay), T103 (TXDELAY, the audio layer's).

- **Expedited datagrams and lone reports** go once the channel has been clear 50 ms after a burst, in the first slot.
- **Ordinary datagrams** wait for the channel to be clear for T101 plus a random T102, drawn afresh each time the channel was last busy. Proposed start: T101 0.8 s, T102 up to 0.4 s, the prototype's draw of 0.8 to 1.2 s.
- Never transmit while busy; a request not yet keyed is withdrawn if the channel turns busy.

**Correction (step 4, packet-net/pdn-soundmodem#892).** One random delay of up to 0.4 s after the first slot separates two waiting stations only if the later one can notice the earlier one's carrier within that delay, and by burst detection alone it cannot (it takes up to 0.9 s). Ordinary traffic therefore draws a whole number of slots, where a slot is the station's own time to notice a carrier, and the delay is not continuous. See "Step 4 as built" for the figures and the collision rates measured.

The prototype's rules, classified:

| Rule | Value | Class |
| --- | --- | --- |
| Clear 0.8 to 1.2 s before data, fresh draw | as stated | Convenience; kept as T101 and T102 |
| After a lone report, the reporter goes on with data; the other leaves the channel clear about 1 s | 1 s | Protocol; kept |
| Wait for the far end after a data burst | 2 s + 0.16 s per symbol | Prototype, sized to a slow decode; replaced (see Choices) |
| Hold-off after a wait that ended in silence | up to 4 s | Convenience; dropped, the random delay covers it |
| A report asked for waits for data to ride on | 1.5 s | Prototype; kept fixed (the protocol says "once the slot has passed") |
| Duty limit | 27 s in 60 s | Prototype safeguard; the audio layer's |
| Burst filled to 2.5 s, never over 9.5 s | | Made-up host and bench rule; see Choices |
| Listen 3 s first; no transmit while decoding | 3 s | Prototype; replaced by the start-up in C and by busy detection |
| Datagram size 240, 16-byte probe at first contact | | Made-up host; not needed |

After a burst with "report wanted", the sender counts a silence if nothing is heard from that station within 3 s of the burst's end (see Choices).

## G. Driving the adaptation rules

- Verdict in: `HeardData(sender, dest, heardFormat, decoded, quality, now, footing, sudden, theirReport, capability)`. Quality from `TrackedReceiver.Quality`, or `PayloadReceiver.MeasureOpening` for a failed burst; footing from `LinkAdaptation.Footing`. A returned report owed to this station is a lone report: "failed" if `Failed`, else "drop". The station adds "asked": decoded, prelude says wanted, `ReportFor` has a value. The rules never read that flag.
- A lone report for this station: `HeardReport`. If it says failed, `BurstNotDecoded` is raised with the tags of the datagrams in this station's latest burst to that sender that ended before the report began. The station knows its own transmissions, so the attribution is exact.
- Sending: `Choose(dest, now)` at the moment the station decides to go, `Sent(dest)` on `TransmitStarted`, `NoAnswer(dest)` when the answer wait runs out. The prelude carries `ReportFor`.
- `PathReport` comes from `HeardReport` and from a prelude with a recommendation; `LinkState` from `TryGetNeighbour` after each call, with "gone" after the library's ten-minute forgetting.

## H. Identification

The callsign is required. On the sample clock, while the station has been transmitting, it sends the identification burst every ten minutes (the interval of the existing Morse ident), counted from the first transmission after the last ident. An idle station never keys to identify. It does not look for the host identifying.

Two gaps: `BurstRenderer.RenderData` accepts only data headers, and the protocol gives the ident payload no byte layout (see Decided).

The Morse ident is the audio layer's job in the program that hosts the modem. The station raises `IdentificationDue` on the same clock and exposes nothing else for it.

## I. Station numbers

How a station comes by its number is not designed (packet-net/pdn-soundmodem#835). Interim: the number is a setting. A name is bound to a number by the host telling the station (proposed `Bind(name, number)`) or by hearing an identification burst. Nothing more is designed here.

## J. What stays out of the library

- Sound cards and their buffers.
- Keying the transmitter and key-up timing (T103).
- Sample-rate conversion.
- The Morse ident's audio.
- The host program's framing (KISS, sockets, AX.25).
- Transmitter duty limits.

## K. Order of building

1. **Clock and plumbing:** sample clock, seeded source, event types, `Skip`, the transmit handshake with a fake audio layer. Proves timers fire at exact sample counts and one seed gives one run.
2. **Streaming detection.** Proves that, at a fixed threshold, candidates equal `BurstFinder.Find` on the same recording at block sizes of 1, 480 and 100000; start-up behaviour; a measured false-candidate rate on noise; a strong burst does not hide a weak one after it. Measures per-second cost.
3. **Reception (built):** header read, ring, job queue (inline for tests), `TrackedReceiver`, `Received`, the `LinkAdaptation` calls. Proves rendered bursts resampled by a few hundred ppm decode at every opening, and results apply in order with two workers.
4. **Busy and channel access (built):** busy union, T101 and T102, request and withdrawal. Proves the station never keys over a burst in progress and waits are exact.
5. **Sending and the simulated channel:** queue, packing, `Choose`, rendering, `Transmitted`; a test channel with delay, noise, path loss and a sample-clock offset. **First step where two stations in one process exchange datagrams**, at first-contact rates.
6. **Reports:** owed and lone reports, `BurstNotDecoded`, silences. Proves two stations settle on a rate, a 10 dB drop costs one burst, and a failure is reported at once when the verdict lands after the first slot.
7. **Identification**, with renderer and payload layout.
8. **Hostile channel:** three stations, capture effect, interference part-way through a burst, a path cut and restored.

## Step 3 as built

Where what was built differs from, or adds to, what the sections above say.

- **Candidates and header reads.** The station feeds the detector in pieces that end at each frame's end, takes the frame's candidates (sorted by index, so that a burst's own start is read before the candidates other openings find inside it), and reads headers **one at a time**, in that order, each only once its opening is in the ring as of the last frame's end. That makes "this candidate lies inside a burst already known, so ignore it" a decision taken with every earlier candidate settled: it is the same whatever the block size and however fast the workers are, and a strong burst's payload costs no reads (the test counts them). A candidate more than a symbol after a known burst's start and before its last sample is ignored unread; one nearer the start is read, and if it reads the same header as a burst already known, within a symbol, it is that burst. A data burst read under an opening other than its format's is not taken for a burst.
- **Pausing the estimate** is by sample index from a header read, to the burst's last sample, not "from a confirmed candidate": the audio of a candidate's own frame has been correlated before the candidate is out. A burst that begins inside one already marked is clipped to begin where that mark ends, since marks must not go backwards.
- **What is not strictly a function of the audio.** Whether a header read has been taken in before the detector correlates the next frame decides whether that frame's values go into the estimate. Inline, and for workers that keep up with the sample clock (a read takes 1 to 2 ms and a frame comes every 0.42 s of audio), it always has; a worker thread that is starved for a frame's worth of audio lets that frame's values into the estimate. The estimate is a 30 s median, so this moves the threshold by a hair and changes a result only if a candidate sits between the two thresholds. Nothing else the station does depends on timing: events and adaptation state do not.
- **Header reads wait for a free worker.** A read takes 1 to 2 ms and the receive of a dense burst took two or three times the burst's own length on one worker (a 2.4 s burst of 8 bits a carrier took about 8 s on a 12-thread desktop; after the tracked receiver stopped repeating work whose output was thrown away, about 1 s, 0.4 of its length: `SM_BENCH=1` in the test project prints the figure for the machine at hand), and `RunOne` takes a waiting header read before a waiting receive but cannot interrupt a receive that has begun. A host with one worker therefore sees the next burst's header read wait behind the receive in progress; nothing is lost while the ring (16.4 s) holds the audio, but the results behind it wait and the pause of the estimate is late. A host should run at least two workers, so that one is free for headers while the others receive.
- **Results** are applied when the burst's job is done and no earlier burst can still turn up: nothing unread or unreleased with an earlier index, and the detector's `CandidateFloor` (the index below which it can give no more candidates) past the burst's start. For a long burst that is long satisfied; for a report it holds the result back by up to about one frame (0.4 s) after the report ends. `Push`, `Skip` and `DeliverResults` apply what is ready, on the audio thread.
- **A burst dropped from a full queue** raises nothing. `Dropped` is the host interface's event for a datagram this station sent and tags it with the host's tag; a burst received has no tag, and the station does not know whether it was for it. It is counted (internal for now) and its place in the order is passed. Step 4's busy detection and step 8's hostile channel may want a visible figure for it.
- **"Asked" reports** (the prelude says "report wanted", the burst decoded, `ReportFor` has a value) are not recorded as owed yet: `ReportFor` marks the neighbour as told, and that belongs with sending, in step 6. What the rules return from `HeardData` (a report "failed" or "drop") is recorded as owed, one for each neighbour, the newest replacing the one before it, with the sample the burst ended at.
- **`LinkState`** is raised after each call on the rules for a neighbour when its format (what `Choose` gives now), the room in a burst at that format or where the format came from changes, and with `Gone` set when ten minutes on the sample clock pass without a word from it. The room is the longest burst's payload less the largest prelude and the datagram's own length: the largest datagram that goes whole, as Choice 5 has it. Where nothing is yet known the format and rate are null.
- **`ReceivedEvent`'s quality** is the mean over the payload's carriers of the measured quality in dB, before the footing is taken off. The datagram's memory is valid only during the event.
- **Names.** A station heard by number alone is named by the ASCII text "#" and the number in decimal in `PathReport` and `LinkState`, and `Received.Sender` is null; step 5's `Bind` replaces what the station hands out, not the event types.
- **A lone report for another station, or for everyone,** is read, takes its place in the order, and is neither measured nor acted on.
- **Skip** feeds silence through the ring and the detector, so a gap in the audio is a gap in the ring too.

## Step 4 as built

Where what was built differs from, or adds to, what sections E and F say. The rules are in `ChannelAccess` (a clock, a seeded source and a few callbacks, so that two of them can be run on one test clock); `Station` joins them to its clock, receive path and audio-layer handshake.

- **Busy.** The union of: a known burst, from the frame that read its header to its last sample; a candidate that will not read, from the candidate's first sample for its opening and one second; an unknown version, one second from the read, again for each header that reads; a candidate waiting to be judged; the station's own transmission, from the grant to `TransmitEnded` plus 100 ms; the host's indication (`SetExternalBusy`), while set and for 100 ms after. Every end is a sample index and a timer on the sample clock, so `ChannelBusy(false)` is raised with the clock at exactly that sample, whatever the block size. `ChannelBusy` is raised on a change of the union only. A burst already over when its header is read (a lone report is found 0.24 to 0.46 s after it ends) shows as busy for the moment it was being judged and clear again at once; the wait that follows is counted from the burst's last sample.
- **Candidates that are the same burst.** A burst is found under each opening whose reference it resembles, and the readings under the wrong openings fail. Left alone, each would hold the channel for a second after every short burst, and each would wait a frame for its opening. A candidate inside a known burst or within a symbol of its start is therefore dropped unread (it is counted with those ignored), is not waited for, and a failed read within a symbol of a known burst's start adds no hold. The holds of failed reads are applied once no candidate is left to judge, so that the order the readings come in does not matter. An unknown type under a known version holds the channel for the length its header carries.
- **The station's own transmission.** From a symbol before the audio layer's reported start to 0.25 s after its reported end, candidates are ignored (counted in `CandidatesMuted`), and the detector's threshold estimate is paused over the transmission and the 0.25 s.
- **The start.** Nothing is granted in the first 5 s, the time the detector's threshold floor alone applies (section C).
- **The time to notice a carrier** is 100 ms (4800 samples) once the host has called `SetExternalBusy` at all, set or clear; before that, the detection latency of the slowest opening: the renderer's lead-in (4224), the later of the reference plus a symbol and the opening the header read needs (19008 on the 1.5 kHz opening), and a frame's new values (20097): 43329 samples, 0.903 s. The lead-in is in it because a station's request starts before the first training sample, and a slot is the gap between two stations' starts. A slot at least this long means a station one slot behind another always hears it first.
- **Ordinary traffic** waits for the channel to be clear for 0.8 s and then a whole number of slots drawn from the seeded source (`Random.Next(window)`) the first time the wait is judged after the channel was last busy. The window is a fixed constant for each case: **4 slots of 100 ms** with a carrier indication (0 to 0.3 s, as near the design's 0.4 s of spread as whole slots get), **2 slots of 0.9 s** with detection alone (0 or 0.9 s: the same count would cost up to 2.7 s of dead air and 1.35 s on average). `ChannelAccess.SlotWindow()` is the seam for packet-net/pdn-soundmodem#892, where the window will follow the stations heard and the collisions seen. If the indication appears mid-wait the draw is kept and the slot shortens (the wait is recomputed, and the request goes at once if the new time has passed); if the channel turns busy first the draw is dropped and made again.
- **Reports and expedited datagrams** go 50 ms after the channel was last busy. **After a lone report for this station** (applied when the report's result is, up to a frame after it ends), ordinary traffic may not be granted before a second after the report's last sample; an ordinary request already handed over and not keyed is taken back. Reports for another station or for everyone hold nothing.
- **Order.** Report (and the identification owed), then expedited, then ordinary, first in first out within a class. One request is handed to the audio layer at a time; the next waits for its end, the 100 ms tail and its own class's wait.
- **Withdrawal.** A granted, not yet keyed request goes back (`WithdrawRequest`) and is queued again, in its place in its class, when any cause other than the station's own turns the channel busy. A request the audio layer started anyway (the race the handshake already tolerates) is on air, is not asked for again, and a second hand-over of it is withdrawn.
- **The entry point** is `Station.Enqueue(samples, class)` (internal; step 5 calls it), returning an id, with an internal event `AccessChanged` for granted, withdrawn, started and ended. `RequestTransmit` and `WithdrawTransmit` remain as the handshake below it.
- **Threads.** `SetExternalBusy` and `Enqueue` are audio-thread members, serialised with `Push`, and, like `TransmitStarted` and `TransmitEnded`, may not be called from an event handler or timer action. They raise events on the calling thread. No worker member changes.
- **What the correction measured** (two stations, a third station's burst ending at the same sample for both, each with an ordinary datagram waiting, 4000 rounds, the other's transmission noticed at the first frame boundary after its opening is in, the opening drawn at random, each station's frame phase its own; with an indication, it follows the carrier within 100 ms): with detection alone the original single delay (0 to 0.4 s) had both on air at once in 98.2 percent of rounds, the slotted draw in 48.8 percent. With a carrier indication, 23.2 and 25.9 percent. The slotted figures are the chance of two stations drawing the same slot, one in the window, as theory says: a station one slot behind always hears the other first. The slots cost something where a continuous delay is already enough (with an indication the slotted draw collides a little more often, not less), and the window is the lever #892 will move.

## Decided (Tom, 26 November 2026)

1. The shape is approved and is to be built in the order above: the station's only clock is the count of audio samples received, the library has no threads of its own, and the host interface is defined here, in the library, so that any program can use the modem.
2. The identification burst's payload is the callsign as plain text behind a length byte, then the 16-bit capability word. It goes into the protocol document with step 7.

## Choices this design makes

Settled without being put to Tom, because each is a starting value or an internal matter. Each is a fixed value in the code, not a setting, and the bench may move it.

1. **First slot and random delay:** the prototype's draw, 0.8 s plus up to 0.4 s. The protocol sets no figure.
2. **Counting a silence:** a fixed 3 s after the burst's end, to be reset once the decode time of this implementation is measured.
3. **Decode work is run by the caller.** No threads in the library, and tests are deterministic.
4. **A candidate whose header will not read** holds the channel busy for the opening plus one second, then is judged again.
5. **One burst's length:** a ceiling of airtime (the prototype's 2.5 s to start) or one datagram, whichever is the longer, so that a datagram that needs a long burst still goes whole.
6. **`Choose` is called only once the channel is free**, and the burst is rendered at once, so there is nothing to undo when a request is withdrawn.
7. **Transmitter duty limits belong to the audio layer**, which knows the transmitter.
