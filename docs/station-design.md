# The live station: the design

Status: a design, approved by Tom on 2026-11-26 (see Decided); step 1 of K is built (the clock, timers and transmit handshake in `Station`), the rest is not. Every name marked "proposed" is a suggestion, not an existing API. It designs what this library lacks: something that takes received audio as it arrives, finds and decodes bursts, decides when the channel is free, decides what to send and when, and talks to a host program. The protocol is [adaptation-protocol.md](adaptation-protocol.md). The operations and events follow the [host interface](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ofdm-fm/host-interface.md) in packet-net/pdn-soundmodem.

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

1. **Correlation in overlapping blocks.** The audio is transformed once per block and shared. Each opening has a precomputed reference spectrum (1, 3 or 6 symbols: 2112, 6336, 12672 samples) and its own inverse transform. The energy under the reference comes from a running sum of squares, so the normalised value is the one `Find` computes.
2. **Threshold:** the larger of 0.1 and 7 times a running median of the correlation (every 16th value, as `Find` does) over the last 30 s, kept as a histogram. The estimate pauses from a confirmed candidate to the burst's end, and during the station's own transmissions, so a strong burst does not lift it. For the first 5 s only 0.1 applies, and the station does not transmit.
3. **The same peak rule, one symbol late:** a value is confirmed once it is over threshold and the largest within a symbol either side.
4. **Header read** when the opening's samples are in: 4224 samples on the full band, up to 19008 (0.4 s) on the 1.5 kHz opening. `HeaderReader.TryRead` gates the candidate. Candidates of different openings reading the same header within a symbol are one burst. `PayloadReceiver.SamplesNeeded(header)` then gives the burst's length.

Buffering: a ring of about 16 s. The longest burst is 261 symbols, 11.5 s, plus lead-in and a pad either side for the resampler. A complete burst is copied to a pooled buffer for its job, so the ring keeps moving.

Cost per second of audio, in existing pieces: one forward and three inverse transforms per block, a running sum, the histogram, and a header read per candidate. Not measured; step 2 measures it. False candidates are the risk. A station is deaf to a second burst for up to 0.4 s after one starts.

## D. Decoding without blocking the audio

`Push` does correlation and peak rule only. Header reads and payload receives are jobs.

- The station queues jobs. The caller runs them on threads of its choosing: proposed `HasWork`, `RunPending()`, `RunOne()`. Each thread has its own `TrackedReceiver` (large buffers, not thread-safe). A proposed `inline` mode runs jobs inside `Push` for tests.
- A burst arriving mid-decode is queued first in, first out, its audio already copied. More than four queued bursts: the oldest is dropped and `Dropped` is raised for what it owed. 
- Results are applied in order of burst start, whichever job finishes first, and `LinkAdaptation` is given the burst's end time on the sample clock, not the verdict's. Decode speed never changes what the rules see.
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
3. **Reception:** header read, ring, job queue (inline for tests), `TrackedReceiver`, `Received`, the `LinkAdaptation` calls. Proves rendered bursts resampled by a few hundred ppm decode at every opening, and results apply in order with two workers.
4. **Busy and channel access:** busy union, T101 and T102, request and withdrawal. Proves the station never keys over a burst in progress and waits are exact.
5. **Sending and the simulated channel:** queue, packing, `Choose`, rendering, `Transmitted`; a test channel with delay, noise, path loss and a sample-clock offset. **First step where two stations in one process exchange datagrams**, at first-contact rates.
6. **Reports:** owed and lone reports, `BurstNotDecoded`, silences. Proves two stations settle on a rate, a 10 dB drop costs one burst, and a failure is reported at once when the verdict lands after the first slot.
7. **Identification**, with renderer and payload layout.
8. **Hostile channel:** three stations, capture effect, interference part-way through a burst, a path cut and restored.

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
