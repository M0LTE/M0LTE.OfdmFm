# Losing bursts, and what to do about it: a design

Status: agreed by Tom on 2026-11-27, including the query burst and its one-bit change to the protocol. Not yet built. This is the second form: the first treated another station transmitting as the only alternative to a weaker path, and Tom sent it back as too simple. It answers packet-net/pdn-soundmodem#914 and the access-rule faults #899, #901 and #902, with #892 folded in. Prompted in part by Perry M0PYL's observations that slowing down is the wrong answer to interference, that speeding up can be the right one, and that trying a faster rate now and then is worth doing.

## What the reading found

Two pieces of background reading were done for this, on what a VHF FM channel does to a signal and on rate control when losses have several causes. Most of what came back is textbook and standards material, and some is informed estimate. **No published measurement was found of how real amateur VHF packet losses divide between collisions, noise and weak signal.** So the figures below set the shape of the design, and the design includes measuring (see "Measure before tuning").

**The channel.**

| Cause | One event lasts | Hits | Shows up as | Same both ways? |
|---|---|---|---|---|
| Path weaker: weather, a lift ending, an antenna | minutes to hours | every burst | signal down across the band, burst after burst | mostly |
| Flutter: aircraft, vehicles, trees | seconds, up to a minute for an aircraft; fades at about 1 to 50 Hz | part of a long burst, or all of a short one | quality rising and falling inside the burst and between bursts | yes |
| Impulsive noise: ignition, power lines, switching supplies | milliseconds each; a source may run for hours | a few symbols | isolated bad symbols in a clean burst | no, it is local to one receiver |
| Raised noise floor: broadband hash, a strong signal nearby | as long as the source runs | every burst | noise up on some or all carriers | no |
| Another station on the frequency, heard or hidden | its transmission: 0.2 s to tens of seconds | part or all | signal unchanged, then errors together from some point on; or the burst never found | no |
| Something else holding the channel: voice, another mode, a distant station during a lift | seconds to hours | everything while it lasts | the channel busy with nothing readable | no |
| The radios themselves: key-up or squelch clipping the front | every burst | the opening | openings damaged the same way each time | per radio |

Man-made noise in a town is put at roughly 13 to 17 dB above thermal at 144 MHz and 25 to 30 dB at 50 MHz (ITU-R P.372), so on 6 m the receiver's own noise is rarely what limits it. An FM receiver is captured by the stronger of two signals once it leads by a few dB (figures of 2 to 10 dB are quoted for real radios), so an interferer does not add noise, it replaces the signal. The Reading to Winchester path of the field trial (#917) lies under busy airspace, so aircraft flutter should be expected there.

**Rate control elsewhere.** What was found, and how far each point was checked:

- **Treating every loss as a weak channel collapses under load**, because a lower rate is on the air longer and collides more. Stated outright in a survey of Wi-Fi rate control (arXiv 1807.02827) and the starting point of every scheme below.
- **A failed burst can be read at the receiver.** COLLIE (Rayanchu and others, INFOCOM 2008) looks at where the symbol errors fall in a failed frame: a collision gives long unbroken runs. On real hardware it told collisions from weak signal about 60 percent of the time at a 2 percent false-alarm rate with one receiver. SoftRate (Vutukuru, Balakrishnan and Jamieson, SIGCOMM 2009) marks interference by an abrupt jump in estimated error rate part-way through a frame and lowers the rate only for losses that look like fading; it was tested on real hardware. That jump is this modem's existing "sudden" judgement. The lesson from COLLIE's figures: the judgement will sometimes be wrong, so a wrong call must be cheap.
- **A short probe settles what a lost burst could not.** CARA (Kim, Kim, Choi and Qiao, INFOCOM 2006) sends a short request before a retry: if the short one gets through, the long one failed to the channel; if not, to a collision; and it does not lower the rate for collisions. Shown in simulation only, with hidden stations left out. The "query" below is the same idea.
- **The nearest relative is the cellular arrangement**: the receiver reports channel quality and an outer loop trims an offset by results, stepping down on a failure and up a little on each success, the ratio of the steps setting the failure rate it settles at. This modem's margin is that loop (half a decibel wider on a failure, a hundredth narrower on a success, so it settles near one failure in fifty). That unpredicted interference drags such a loop down is described in patents and assumed in papers proposing fixes; no measured account was found.
- **Counting successes at each rate needs traffic a packet channel does not have.** Minstrel, in Linux, samples other rates on about one packet in ten and recomputes ten times a second. Where rates are probed at all, only the neighbours of the current best need trying, because throughput has a single peak across the ordered rates (Combes, Proutiere and others, INFOCOM 2014; simulation and recorded traces).
- **How busy the channel is can set the random wait directly.** Idle Sense (Heusse and others, SIGCOMM 2005) holds the idle time between transmissions at a target and does not use losses as its signal. A later paper reports that its target does not hold where there are hidden stations.
- **That a faster rate loses fewer bursts under contention** is argued widely and supported by two test-bed reports that were found but not read in full. No deliberate "speed up on a collision" rule was found proven on real hardware. It is the least tested idea here.

## Two kinds of loss

Every cause in the table harms a burst in one of two ways, and they call for opposite responses.

- **Too little signal for the format.** Weaker path, raised noise, the bottom of a fade. The answer is a more robust format, which is slower. The modem already does this, from what it measures.
- **Something landed on it.** An impulse, another station, the bad second of a flutter. These arrive at random in time, so the chance of being hit grows with how long the burst is on the air. The answer is to be on the air for less time: shorter bursts, and a faster format if the signal allows it. Slowing down makes it worse.

So for each neighbour a station keeps two things, where today it keeps one:

1. **How much signal there is, and how steady it is.** The estimate it has now, plus a measure of how much successive bursts differ. A path that flutters gets a wider margin because it moves, not because bursts failed.
2. **How often something lands on a burst**, as hits per second of airtime, from the evidence below. It fades with time, so an old bad spell is forgotten.

The format and the burst length are then chosen together, to carry the most on balance: the first number says which formats will decode, the second says how long a burst can be before it is likely to be hit.

## Telling them apart

The receiver has more to go on than a pass or a fail, because it measures every symbol of every burst.

| What the receiver sees | Read as | What changes |
|---|---|---|
| Signal down across the band, at the opening and throughout | weaker path | the estimate follows it, as now |
| Quality rising and falling within the burst, or burst to burst with no trend | flutter | the steadiness measure, and so the margin |
| A few isolated bad symbols in an otherwise clean burst | impulse | a hit; nothing else |
| Clean, then bad from some point to the end (the existing "sudden" judgement) | something landed on it | a hit; nothing else |
| Opening as good as ever, payload failed, at a format that worked in the last 30 s | something landed on it at the start | a hit; nothing else |
| Opening as good as ever, payload failed, at a format not shown to work | the format was too ambitious | the margin, as now |

The sender has its own evidence when nothing comes back:

| What the sender sees | Read as | What it does |
|---|---|---|
| No answer, but the station is still heard as strongly as before | lost at the far end under something | a hit; ask cheaply (below) |
| No answer and nothing heard since | cannot tell | ask cheaply (below) |

Only "too ambitious" and "weaker path" move the margin and the estimate. That is the cure for the outer loop being dragged down by losses it did not cause.

## Ask cheaply before assuming the worst

Today three unanswered bursts send a station to the slowest format, where one frame takes eleven seconds. If the path has gone, those bursts are wasted; if it has not, they are the longest target on the channel.

Proposed: when a burst goes unanswered, the next thing sent to that station is a **query**: a header with no payload, a fifth of a second long, that asks for a report. It is the hardest burst to hit and the easiest to decode.

- **Answered:** the path is there, and the answer carries a fresh measurement. The lost burst is counted as a hit and traffic carries on at what the answer supports.
- **Not answered:** asked again after a wait that doubles each time. Nothing long is sent into silence. Datagrams wait in the queue and expire as they do now, and the host is told.

This needs one spare bit in the report burst's header to mean "report wanted". It replaces the three-silences rule.

## Speeding up

Three ways, all bounded by what the receiver says the signal supports.

1. **Never slow down for a hit.** Follows from the above.
2. **Use the margin when hits are frequent.** The safety margin trades a little rate for fewer failures from too little signal. When bursts are being lost to hits anyway, a faster burst that is on the air for less time loses fewer, so part of the margin is given back: one rung faster while the hit rate is high.
3. **Try the next rung up now and then.** On a path that has been steady and successful, about one burst in twenty goes one rung faster than recommended, chosen at random, and only when the burst is short. If it decodes, the receiver has direct evidence its margin is too wide and narrows it faster than the slow creep it uses now. If it fails, nothing is held against the path: the existing rules already ignore the failure of a format faster than the one recommended. This is the receiver's measurement being checked by experiment, not a search across rates.

## Sharing the channel

Kept from the first form:

- **Always draw a random wait before ordinary traffic** (#899). Today two stations given traffic at the same moment on a quiet channel collide every time.
- **The station just spoken to answers first**, with no random wait, so a two-way exchange is not slowed.
- **Do not start over an answer that was asked for** (#902).
- **Only a report or a burst for this station answers the question** (#901).

Changed:

- **The random wait widens with how busy the channel is seen to be**, from the share of time it is occupied (#910), and not from losses, since a loss to an impulse says nothing about contention.
- **Bursts shorten with the hit rate**, as the two-number choice above gives, in place of a fixed one-second rule.

## What this does not cover

- **A carrier the modem cannot decode**, such as voice on the frequency, is invisible to a station with no carrier indication from its radio. pdn-soundmodem has a detector that judges this from the audio; whether it is quick and sure enough to feed in is a bench question.
- **A stronger burst that begins inside one already being received** (#900).
- **A receiver that cannot hear some carriers at all** (#886): that is about which formats exist for a pair of radios, not about losses.
- **Key-up and squelch clipping** belong to setting up the radios (#826).
- **What the layer above knows** (a resend, an acknowledgement), and **a host naming the format** (#915): not needed by this design.

## Measure before tuning

Every threshold here is a starting value, and the reading could not supply better ones. Three sources of real figures:

1. **The trace** (#925) should say, for every lost burst, which row of the tables it was put in. A day on the air then gives the split nobody has published.
2. **Listening.** A receiver left on a busy 2 m packet frequency gives how often and for how long others hold the channel, how many bursts on it cannot be decoded, and how that moves through a day, with no transmitting at all. Tom's suggestion: a public web receiver, through the OpenWebRX client pdn-soundmodem already has. In England the 2 m packet frequencies are 144.925, 144.9375 and 144.950 MHz, with APRS on 144.800. A web receiver's audio has been through its own demodulator and compression, so it will say little about impulsive noise; that needs a receiver of our own.
3. **The field trial** (#917), with its aircraft.

In simulation the design is judged as before: eight seeds each, three stations and a hidden station, now with impulses and a fluttering path added to the simulated channel. The claims to test: a busy or noisy channel no longer drags links to the slowest format; a quiet two-station link is no slower than today; and a fluttering path settles at a rate it can hold.

## Choices made here

Settled without being put to Tom, because each is internal or a starting value. Each is fixed in the design, not a setting.

1. "Shown to work" means a decode at that format or faster from the same station within the 30 s the rules already use for freshness.
2. "As strongly as before" means within the 2 dB the rules already use for a drop.
3. A hit is forgotten with a half-life of five minutes.
4. One burst in twenty tries the next rung, only on a burst of under a second, only after ten successes in a row.
5. A query is repeated after 2, 4, 8 seconds and so on, up to a minute.

## Sources

Checked against the source or its abstract on 27 November 2026 unless marked.

- Rayanchu, Mishra, Agrawal, Saha, Banerjee, "Diagnosing wireless packet losses in 802.11: separating collision from weak signal", INFOCOM 2008.
- Vutukuru, Balakrishnan, Jamieson, "Cross-layer wireless bit rate adaptation", SIGCOMM 2009.
- Kim, Kim, Choi, Qiao, "CARA: collision-aware rate adaptation for IEEE 802.11 WLANs", INFOCOM 2006 (read from a summary, not the paper).
- Holland, Vaidya, Bahl, "A rate-adaptive MAC protocol for multi-hop wireless networks", MobiCom 2001.
- Combes, Proutiere, Yun, Ok, Yi, "Optimal rate sampling in 802.11 systems", INFOCOM 2014.
- Heusse, Rousseau, Guillier, Duda, "Idle Sense: an optimal access method for high throughput and fairness in rate diverse wireless LANs", SIGCOMM 2005.
- The Linux kernel's documentation of the Minstrel rate control algorithm.
- "Lessons learned and looking forward", a survey of Wi-Fi rate adaptation, arXiv 1807.02827.
- ITU-R P.372 (radio noise). The figures quoted were computed from its formula as remembered, not read from the current edition.
- IK2PIH, "APRS performance and limits", TAPR Digital Communications Conference 2020, which notes that measured collision data for real amateur channels is missing.
- Not checked, and so not relied on: the outer-loop step formula beyond patents; the twice-the-frame-time exposure to a hidden station (Tobagi and Kleinrock 1975); fade rates and depths for aircraft flutter, which are from amateur accounts and two-ray theory; capture ratios of real radios.

## Baseline: how the station behaves today

28 November 2026: the figures in this section were recorded before identification moved to the front of a station's first transmission (library 0.3.0), and will be rerun with the next loss slice.

Recorded on 28 November 2026, before any change to the station, so that later changes can be judged against it. Nothing here is tuned: the scenarios were fixed before the results were seen, and every figure is a total or a range over the same eight seeds (1 to 8) the earlier comparisons use. The figures describe a simulated channel (see "What the simulated channel does and does not do"); they are not predictions for a real one. One row for each seed of every scenario is in `docs/channel-sharing-baseline-by-seed.md`.

### How it was measured

- **Traffic.** Datagrams of 100 bytes, at random moments, both ways between each pair of stations that talk: one every 8 s on average each way for two stations, one every 24 s each way for each pair of three stations, one every 12 s each way for each pair in the hidden-station arrangement. Traffic runs for 5 minutes (2.5 for flutter, 4 for the step), then the stations are given 30 to 60 s to empty their queues. The hosts do not resend anything, so a lost burst shows as datagrams not delivered. The station's own carrier detection only (no carrier indication from a radio).
- **The paths.** Two stations: a good path of -48 dB (a gain of 0.004 against the simulator's usual receiver noise). The simulator's usual path, at 0 dB, is far stronger than any format needs; in a probe on one seed of the control the station used its fastest formats down to about -46 dB, was in the middle of its formats around -54 dB and had no working path at -66 dB, so -48 dB is a good path from which a 12 dB loss still leaves a usable one. Three stations: paths of 0, -10 and -20 dB between the pairs, as in the earlier three-station run. Hidden station: two stations that cannot hear each other, each 0 dB from a third.
- **Impulses.** No published figures were found for rates and levels, so three grades were chosen to span from "most bursts untouched" to "every burst hit several times". Light: 0.5 a second, 6 dB over the wanted signal. Moderate: 2 a second, 10 dB over. Heavy: 8 a second, 16 dB over. One event in ten is a train (50 to 200 impulses a second for 0.1 to 1 s). A burst of a second or two sees about 1, 4 and 16 impulses at the three grades, and a single impulse of a few milliseconds spoils one or two symbols of about 44 ms. In the three-station and hidden-station runs the level is 10 dB over the signal on the strongest path, at every receiver. Those two arrangements use the paths of the earlier short runs with steady traffic in place of their eighteen datagrams.
- **Flutter.** An episode of 30 s starting 60 s in, on the path between the first two stations, both ways, each way with its own starting phase. Fade rates of 3 Hz and 20 Hz, and a crossing that starts at 20 Hz, falls through zero half way and climbs back. Reflected strengths of 0.5 (fades 9.5 dB deep) and 0.9 (about 26 dB).
- **The step.** 12 dB more loss both ways from 90 s, and the 12 dB back from 150 s. It takes effect from the next burst on the air.
- **Bands of airtime.** By the rate of the format each burst is sent at: the slowest format (the one first contact uses, 0.25 kbit/s), slow (under 1 kbit/s), middle (1 to under 10 kbit/s) and fast (10 kbit/s and over). The first burst each way between a pair is always at the slowest format, about 9 s of airtime in a two-station run, so that band has a floor.
- **What the simulator knows.** A burst counts as not decoded when the station it was for delivered none of its datagrams. Each such burst is given one cause, taking these in turn: the path's steady gain was below what the burst's format needs (found by asking the receiver: the lowest gain at which that format decodes in five of six tries over the simulator's usual noise); another station's burst, or the receiver's own transmission, overlapped it; the flutter took the gain below what the format needs; an impulse overlapped it; none of these. "Bursts an impulse overlapped" counts every burst an impulse touched, decoded or not, so it can exceed the bursts lost to impulses.
- **Falls to the slowest format.** A burst at the slowest format to an address after one at another format to the same address. It counts as caused by weak signal when the path's steady gain was then below what the format the station fell from needs; the others are the falls the design wants to drive to zero.
- **Silences and addresses lost.** Silences are the times a burst asked for a report and no answer came and it was counted. An address lost is the station forgetting where an address lives, after three silences.
- **Run time.** 136 runs (17 scenarios, 8 seeds) took 19 minutes on the build box with three at a time. No simulated durations were cut.

### What the simulated channel does and does not do

- **Impulses** are bursts of white noise of 0.2 to 5 ms on one receiver's input, arriving at random at a given mean rate, as ignition or switching noise would. They are per receiver, so two stations hear different ones. Not modelled: the shape of a real FM click, and any link between the impulse and the signal level.
- **Flutter** is the sum of a direct path and one reflected path whose phase rotates, applied as a gain on the signal against the receiver's fixed noise. The mean power is kept the same, so the fades are the only difference. Not modelled: capture and the threshold effects of an FM receiver below its threshold, and more than one reflection.
- **A step** changes the path's loss at a given moment, from the next burst.
- **Ground truth** is exact for what the simulator did and approximate for what a burst needed: the need of each format is measured once over one path's noise, with a steady signal. A burst that needed a little more than that on a path with two inbound noise sources is counted with the noise scaled to match.

All three are off unless a scenario turns them on, are drawn from the simulator's sample clock and seeds, and give the same samples whatever the block size.


### The results

Each table gives the total over the eight seeds and, beside it, the lowest and highest value any one seed gave. Where a figure is a percentage the total is of the pooled runs.

### Impulses on a steady good path between two stations

#### Control: a steady good path, no impulses

The reference for the four below: two stations a good path apart, steady traffic both ways for five minutes, nothing added to the channel. About one datagram in five is not delivered even so. Most of those are bursts that overlapped the other station's (the two stations key at the same moment, or one starts while the other is on the air), and the rest are bursts sent at a format the path could not hold, which looks like the station trying a faster format than the path allows.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 630 | 67 to 94 |
| Datagrams delivered | 510 | 45 to 82 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 120 | 6 to 22 |
| Useful throughput, bytes a second | 17.7 | 12.5 to 22.8 |
| Airtime at the slowest format, % | 23.3 | 11.3 to 42.1 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 2.8 | 0.5 to 7.0 |
| Airtime at the fast, 10 kbit/s and over, % | 74.0 | 51.1 to 87.4 |
| Data bursts sent | 600 | 65 to 90 |
| Bursts not decoded | 114 | 6 to 21 |
| not decoded: path too weak for the format | 35 | 2 to 8 |
| not decoded: another station's burst (or its own transmission) overlapped | 77 | 2 to 17 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 2 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 1 | 0 to 1 |
| of those, with no weak-signal cause | 1 | 0 to 1 |
| Silences counted | 14 | 0 to 8 |
| Addresses lost | 1 | 0 to 1 |

#### Light impulses: 0.5 a second, 6 dB over the wanted signal

Impulses at half a second, 6 dB over the wanted signal, one in ten a train. About one burst in five has an impulse on it. Delivery is about the same as the control, but the station has already slowed: fast formats carry 59 percent of airtime against 74, and the middle band 15 against 3.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 630 | 67 to 94 |
| Datagrams delivered | 497 | 48 to 79 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 133 | 7 to 20 |
| Useful throughput, bytes a second | 17.3 | 13.3 to 21.9 |
| Airtime at the slowest format, % | 25.7 | 10.9 to 56.5 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 15.2 | 4.5 to 24.4 |
| Airtime at the fast, 10 kbit/s and over, % | 59.0 | 30.6 to 81.5 |
| Data bursts sent | 595 | 60 to 90 |
| Bursts not decoded | 129 | 7 to 20 |
| not decoded: path too weak for the format | 26 | 1 to 6 |
| not decoded: another station's burst (or its own transmission) overlapped | 66 | 2 to 14 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 36 | 1 to 8 |
| not decoded: none of these | 1 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 115 | 6 to 20 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 9 | 0 to 5 |
| Addresses lost | 0 | 0 to 0 |

#### Moderate impulses: 2 a second, 10 dB over the wanted signal

Impulses at two a second, 10 dB over the wanted signal. More than half of all bursts have an impulse on them, and an impulse alone costs about one burst in seven. Throughput is 87 percent of the control's. The station keeps off the fastest formats (28 percent of airtime) and spends half its airtime in the middle band, where the bursts are longer and so more exposed.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 630 | 67 to 94 |
| Datagrams delivered | 444 | 43 to 68 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 186 | 16 to 30 |
| Useful throughput, bytes a second | 15.4 | 11.9 to 18.9 |
| Airtime at the slowest format, % | 19.6 | 9.1 to 35.9 |
| Airtime at the slow, under 1 kbit/s, % | 2.3 | 0.0 to 12.2 |
| Airtime at the middle, 1 to 10 kbit/s, % | 50.4 | 25.1 to 62.9 |
| Airtime at the fast, 10 kbit/s and over, % | 27.7 | 19.3 to 52.9 |
| Data bursts sent | 595 | 65 to 89 |
| Bursts not decoded | 178 | 14 to 28 |
| not decoded: path too weak for the format | 10 | 0 to 3 |
| not decoded: another station's burst (or its own transmission) overlapped | 77 | 4 to 22 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 88 | 6 to 15 |
| not decoded: none of these | 3 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 337 | 35 to 47 |
| Times the station fell to the slowest format | 1 | 0 to 1 |
| of those, with no weak-signal cause | 1 | 0 to 1 |
| Silences counted | 16 | 0 to 8 |
| Addresses lost | 1 | 0 to 1 |

#### Heavy impulses: 8 a second, 16 dB over the wanted signal

Impulses at eight a second, 16 dB over the wanted signal. Every burst has an impulse on it. The station never reaches the fast formats and spends over half its airtime below 1 kbit/s. Throughput is 69 percent of the control's. Bursts lost to another station's burst nearly double (138 against 77): slower formats make longer bursts, and longer bursts overlap more. Five datagrams were dropped, and the station fell to the slowest format seven times with the path nowhere near too weak.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 630 | 67 to 94 |
| Datagrams delivered | 350 | 33 to 53 |
| Datagrams dropped by the station | 5 | 0 to 5 |
| Datagrams lost on the air or still queued at the end | 275 | 26 to 46 |
| Useful throughput, bytes a second | 12.2 | 9.2 to 14.7 |
| Airtime at the slowest format, % | 19.1 | 2.3 to 39.8 |
| Airtime at the slow, under 1 kbit/s, % | 56.1 | 31.9 to 82.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 24.8 | 8.2 to 50.5 |
| Airtime at the fast, 10 kbit/s and over, % | 0.0 | 0.0 to 0.0 |
| Data bursts sent | 564 | 62 to 82 |
| Bursts not decoded | 248 | 22 to 38 |
| not decoded: path too weak for the format | 0 | 0 to 0 |
| not decoded: another station's burst (or its own transmission) overlapped | 138 | 7 to 28 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 110 | 8 to 23 |
| not decoded: none of these | 0 | 0 to 0 |
| Bursts an impulse overlapped, decoded or not | 563 | 62 to 82 |
| Times the station fell to the slowest format | 7 | 0 to 2 |
| of those, with no weak-signal cause | 7 | 0 to 2 |
| Silences counted | 41 | 1 to 13 |
| Addresses lost | 3 | 0 to 2 |

### A flutter episode on a good path between two stations

#### Control: a steady good path, no flutter

The reference for the flutter runs: the same two stations and steady good path, a run of 3 minutes with the episode window (60 s to 90 s) marked. Nothing is added. In the window 48 of 61 datagrams were delivered.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 306 | 30 to 49 |
| Datagrams delivered | 257 | 24 to 43 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 49 | 3 to 10 |
| Useful throughput, bytes a second | 17.8 | 13.3 to 23.9 |
| Airtime at the slowest format, % | 34.2 | 21.3 to 43.1 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 2.7 | 1.8 to 4.3 |
| Airtime at the fast, 10 kbit/s and over, % | 63.1 | 53.6 to 76.3 |
| Data bursts sent | 289 | 30 to 44 |
| Bursts not decoded | 47 | 3 to 10 |
| not decoded: path too weak for the format | 21 | 1 to 4 |
| not decoded: another station's burst (or its own transmission) overlapped | 24 | 0 to 6 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 2 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 2 | 0 to 1 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 61 | 2 to 11 |
| delivered | 48 | 2 to 9 |
| Data bursts during the impairment | 57 | 2 to 9 |
| not decoded | 14 | 0 to 4 |

#### Flutter at 3 Hz, reflected path at 0.5 (9.5 dB fades), 30 s

A reflected path half the strength of the direct one, fading 9.5 dB peak to trough three times a second, for 30 s. Only 16 of the 61 datagrams offered in the episode were delivered, and 43 of its 59 bursts were not decoded; a fade accounts for 31 of the lost bursts over the run. The station reacts mostly by using the middle band (18 percent of airtime against 3).

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 306 | 30 to 49 |
| Datagrams delivered | 228 | 21 to 36 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 78 | 4 to 15 |
| Useful throughput, bytes a second | 15.8 | 11.7 to 20.0 |
| Airtime at the slowest format, % | 32.6 | 20.5 to 41.6 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 18.5 | 11.0 to 28.6 |
| Airtime at the fast, 10 kbit/s and over, % | 48.9 | 36.9 to 65.9 |
| Data bursts sent | 289 | 30 to 43 |
| Bursts not decoded | 73 | 4 to 15 |
| not decoded: path too weak for the format | 10 | 0 to 3 |
| not decoded: another station's burst (or its own transmission) overlapped | 28 | 0 to 9 |
| not decoded: fell in a fade | 31 | 1 to 5 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 4 | 0 to 2 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 3 | 0 to 1 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 61 | 2 to 11 |
| delivered | 16 | 0 to 5 |
| Data bursts during the impairment | 59 | 2 to 9 |
| not decoded | 43 | 1 to 8 |

#### Flutter at 3 Hz, reflected path at 0.9 (26 dB fades), 30 s

As above with a reflected path at nine tenths, fading about 26 dB. Much the same damage: 14 of 61 datagrams delivered in the episode, 36 bursts lost to a fade. One seed went into the slow band for 22 percent of its airtime; the others did not, so the response to a deep fade varies a good deal from seed to seed.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 306 | 30 to 49 |
| Datagrams delivered | 222 | 21 to 35 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 84 | 6 to 16 |
| Useful throughput, bytes a second | 15.4 | 11.7 to 19.4 |
| Airtime at the slowest format, % | 30.0 | 16.9 to 40.8 |
| Airtime at the slow, under 1 kbit/s, % | 5.6 | 0.0 to 22.4 |
| Airtime at the middle, 1 to 10 kbit/s, % | 21.0 | 7.1 to 38.5 |
| Airtime at the fast, 10 kbit/s and over, % | 43.4 | 32.5 to 65.9 |
| Data bursts sent | 290 | 30 to 44 |
| Bursts not decoded | 79 | 6 to 16 |
| not decoded: path too weak for the format | 11 | 0 to 3 |
| not decoded: another station's burst (or its own transmission) overlapped | 32 | 0 to 9 |
| not decoded: fell in a fade | 36 | 1 to 7 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 0 | 0 to 0 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 4 | 0 to 1 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 61 | 2 to 11 |
| delivered | 14 | 0 to 5 |
| Data bursts during the impairment | 58 | 2 to 9 |
| not decoded | 45 | 1 to 9 |

#### Flutter at 20 Hz, reflected path at 0.5 (9.5 dB fades), 30 s

Fades at 20 Hz, 9.5 dB deep. Fades at this rate are less damaging than the slow ones above (probably because each lasts about one symbol, not several, but this was not tested): 26 of 61 datagrams delivered in the episode, 23 bursts lost to a fade.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 306 | 30 to 49 |
| Datagrams delivered | 238 | 24 to 42 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 68 | 6 to 13 |
| Useful throughput, bytes a second | 16.5 | 13.3 to 23.3 |
| Airtime at the slowest format, % | 33.3 | 20.4 to 43.1 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 21.3 | 11.2 to 30.8 |
| Airtime at the fast, 10 kbit/s and over, % | 45.4 | 30.8 to 62.6 |
| Data bursts sent | 291 | 30 to 44 |
| Bursts not decoded | 66 | 6 to 13 |
| not decoded: path too weak for the format | 15 | 1 to 3 |
| not decoded: another station's burst (or its own transmission) overlapped | 27 | 0 to 7 |
| not decoded: fell in a fade | 23 | 0 to 5 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 1 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 4 | 0 to 2 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 61 | 2 to 11 |
| delivered | 26 | 1 to 8 |
| Data bursts during the impairment | 58 | 2 to 9 |
| not decoded | 36 | 1 to 7 |

#### Flutter at 20 Hz, reflected path at 0.9 (26 dB fades), 30 s

Fades at 20 Hz, 26 dB deep. 32 of 61 datagrams delivered in the episode, 17 bursts lost to a fade. Deeper made little difference at this rate.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 306 | 30 to 49 |
| Datagrams delivered | 247 | 24 to 43 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 59 | 5 to 11 |
| Useful throughput, bytes a second | 17.2 | 13.3 to 23.9 |
| Airtime at the slowest format, % | 33.1 | 19.6 to 42.0 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 23.6 | 12.1 to 31.2 |
| Airtime at the fast, 10 kbit/s and over, % | 43.3 | 31.2 to 56.7 |
| Data bursts sent | 291 | 30 to 44 |
| Bursts not decoded | 58 | 5 to 11 |
| not decoded: path too weak for the format | 12 | 0 to 3 |
| not decoded: another station's burst (or its own transmission) overlapped | 29 | 0 to 7 |
| not decoded: fell in a fade | 17 | 0 to 4 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 0 | 0 to 0 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 3 | 0 to 1 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 61 | 2 to 11 |
| delivered | 32 | 1 to 8 |
| Data bursts during the impairment | 58 | 2 to 9 |
| not decoded | 30 | 1 to 6 |

#### Flutter sweeping from 20 Hz through zero and back, reflected path at 0.5 (9.5 dB fades), 30 s

An aircraft crossing: the fade rate starts at 20 Hz, falls through zero half way and climbs back, 9.5 dB deep. 32 of 61 datagrams delivered in the episode, 16 bursts lost to a fade.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 306 | 30 to 49 |
| Datagrams delivered | 247 | 24 to 40 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 59 | 4 to 12 |
| Useful throughput, bytes a second | 17.2 | 13.3 to 22.2 |
| Airtime at the slowest format, % | 33.5 | 20.7 to 42.9 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 18.2 | 11.2 to 24.2 |
| Airtime at the fast, 10 kbit/s and over, % | 48.3 | 38.1 to 58.7 |
| Data bursts sent | 290 | 30 to 44 |
| Bursts not decoded | 56 | 4 to 12 |
| not decoded: path too weak for the format | 13 | 0 to 3 |
| not decoded: another station's burst (or its own transmission) overlapped | 23 | 0 to 7 |
| not decoded: fell in a fade | 16 | 1 to 3 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 4 | 0 to 2 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 1 | 0 to 1 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 61 | 2 to 11 |
| delivered | 32 | 1 to 6 |
| Data bursts during the impairment | 58 | 2 to 9 |
| not decoded | 29 | 1 to 6 |

#### Flutter sweeping from 20 Hz through zero and back, reflected path at 0.9 (26 dB fades), 30 s

The same crossing with 26 dB fades. 33 of 61 datagrams delivered in the episode, 19 bursts lost to a fade. One seed spent a third of its airtime in the slow band.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 306 | 30 to 49 |
| Datagrams delivered | 243 | 23 to 43 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 63 | 5 to 11 |
| Useful throughput, bytes a second | 16.9 | 12.8 to 23.9 |
| Airtime at the slowest format, % | 29.2 | 19.2 to 43.1 |
| Airtime at the slow, under 1 kbit/s, % | 8.6 | 0.0 to 33.3 |
| Airtime at the middle, 1 to 10 kbit/s, % | 24.9 | 11.2 to 34.5 |
| Airtime at the fast, 10 kbit/s and over, % | 37.3 | 16.8 to 58.0 |
| Data bursts sent | 292 | 30 to 44 |
| Bursts not decoded | 62 | 5 to 11 |
| not decoded: path too weak for the format | 15 | 1 to 4 |
| not decoded: another station's burst (or its own transmission) overlapped | 28 | 0 to 8 |
| not decoded: fell in a fade | 19 | 0 to 4 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 0 | 0 to 0 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 3 | 0 to 2 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 61 | 2 to 11 |
| delivered | 33 | 1 to 8 |
| Data bursts during the impairment | 57 | 2 to 9 |
| not decoded | 29 | 1 to 6 |

### A step in the path between two stations

#### Control: a steady good path, no step

The reference for the step: two stations, a good path, 4 minutes, the window (90 s to 150 s) marked. In the window 114 of 135 datagrams were delivered.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 517 | 56 to 78 |
| Datagrams delivered | 440 | 44 to 66 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 77 | 5 to 12 |
| Useful throughput, bytes a second | 20.4 | 16.3 to 24.4 |
| Airtime at the slowest format, % | 23.0 | 13.5 to 30.4 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 3.1 | 0.6 to 10.1 |
| Airtime at the fast, 10 kbit/s and over, % | 73.9 | 62.3 to 84.9 |
| Data bursts sent | 489 | 52 to 74 |
| Bursts not decoded | 75 | 5 to 12 |
| not decoded: path too weak for the format | 36 | 3 to 6 |
| not decoded: another station's burst (or its own transmission) overlapped | 35 | 0 to 7 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 4 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 5 | 0 to 1 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 135 | 11 to 22 |
| delivered | 114 | 9 to 21 |
| Data bursts during the impairment | 130 | 10 to 21 |
| not decoded | 20 | 0 to 4 |

#### The path loses 12 dB at 90 s and gets it back at 150 s

The path loses 12 dB at 90 s and gets it back at 150 s. This is the loss the station is built for, and it copes: 97 of 135 datagrams delivered in the window against 114 in the control, the station moves down to the middle band (45 percent of airtime) and back, never falls to the slowest format, and throughput over the run is within 2 percent of the control's. Over the whole run it lost a few more bursts to the path being too weak for the format (41 against 36).

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 517 | 56 to 78 |
| Datagrams delivered | 431 | 45 to 66 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 86 | 4 to 16 |
| Useful throughput, bytes a second | 20.0 | 16.7 to 24.4 |
| Airtime at the slowest format, % | 18.6 | 10.6 to 24.4 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 45.1 | 34.6 to 54.3 |
| Airtime at the fast, 10 kbit/s and over, % | 36.3 | 31.0 to 53.7 |
| Data bursts sent | 486 | 51 to 74 |
| Bursts not decoded | 85 | 4 to 16 |
| not decoded: path too weak for the format | 41 | 3 to 7 |
| not decoded: another station's burst (or its own transmission) overlapped | 42 | 0 to 11 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 2 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 8 | 0 to 2 |
| Addresses lost | 0 | 0 to 0 |
| Datagrams offered during the impairment | 135 | 11 to 22 |
| delivered | 97 | 9 to 16 |
| Data bursts during the impairment | 127 | 10 to 21 |
| not decoded | 37 | 2 to 10 |

### Three stations and a hidden station, with and without impulses

#### Control: three stations that all hear each other, no impulses

Three stations that all hear each other (paths of 0, -10 and -20 dB), steady traffic between every pair, nothing added. Seven in eight datagrams get through. Nearly all of the bursts lost were overlapped by another station's burst (69 of 73).

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 624 | 67 to 91 |
| Datagrams delivered | 546 | 59 to 75 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 78 | 3 to 19 |
| Useful throughput, bytes a second | 19.0 | 16.4 to 20.8 |
| Airtime at the slowest format, % | 28.4 | 14.1 to 41.4 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 0.3 | 0.0 to 2.2 |
| Airtime at the fast, 10 kbit/s and over, % | 71.3 | 58.6 to 85.9 |
| Data bursts sent | 610 | 67 to 88 |
| Bursts not decoded | 73 | 3 to 17 |
| not decoded: path too weak for the format | 0 | 0 to 0 |
| not decoded: another station's burst (or its own transmission) overlapped | 69 | 3 to 17 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 4 | 0 to 2 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 8 | 0 to 4 |
| Addresses lost | 0 | 0 to 0 |

#### Three stations that all hear each other, moderate impulses

The same three stations with moderate impulses on every receiver, two a second, 10 dB over the signal on the strongest path. Delivery falls from 88 to 55 percent and throughput to 63 percent of the control's, a bigger fall than the two-station case, and the slower formats the stations drop to make longer bursts, which another station's burst overlaps more often. Silences counted go from 8 to 55, the station fell to the slowest format 3 times and lost 2 addresses, none with a weak path. The share of airtime in the slow band ranges from nothing to 70 percent across seeds.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 624 | 67 to 91 |
| Datagrams delivered | 344 | 34 to 52 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 280 | 31 to 46 |
| Useful throughput, bytes a second | 11.9 | 9.4 to 14.4 |
| Airtime at the slowest format, % | 25.3 | 8.6 to 35.1 |
| Airtime at the slow, under 1 kbit/s, % | 25.5 | 0.0 to 69.5 |
| Airtime at the middle, 1 to 10 kbit/s, % | 26.3 | 9.6 to 44.5 |
| Airtime at the fast, 10 kbit/s and over, % | 23.0 | 10.1 to 45.3 |
| Data bursts sent | 601 | 64 to 88 |
| Bursts not decoded | 270 | 30 to 44 |
| not decoded: path too weak for the format | 0 | 0 to 0 |
| not decoded: another station's burst (or its own transmission) overlapped | 92 | 2 to 18 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 176 | 17 to 30 |
| not decoded: none of these | 2 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 364 | 36 to 55 |
| Times the station fell to the slowest format | 3 | 0 to 2 |
| of those, with no weak-signal cause | 3 | 0 to 2 |
| Silences counted | 55 | 3 to 13 |
| Addresses lost | 2 | 0 to 2 |

#### Control: a hidden station, no impulses

Two stations that cannot hear each other, both talking to a third that hears both, nothing added. Seven in eight datagrams get through; almost all the losses are overlaps (85 of 92 bursts not decoded), the hidden station's bursts landing on the others.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 807 | 89 to 113 |
| Datagrams delivered | 711 | 78 to 95 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 96 | 6 to 18 |
| Useful throughput, bytes a second | 24.7 | 21.7 to 26.4 |
| Airtime at the slowest format, % | 30.1 | 10.8 to 62.1 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the fast, 10 kbit/s and over, % | 69.9 | 37.9 to 89.2 |
| Data bursts sent | 773 | 86 to 107 |
| Bursts not decoded | 92 | 6 to 17 |
| not decoded: path too weak for the format | 0 | 0 to 0 |
| not decoded: another station's burst (or its own transmission) overlapped | 85 | 5 to 17 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 0 | 0 to 0 |
| not decoded: none of these | 7 | 0 to 3 |
| Bursts an impulse overlapped, decoded or not | 0 | 0 to 0 |
| Times the station fell to the slowest format | 1 | 0 to 1 |
| of those, with no weak-signal cause | 1 | 0 to 1 |
| Silences counted | 15 | 0 to 7 |
| Addresses lost | 1 | 0 to 1 |

#### A hidden station, moderate impulses

The hidden-station arrangement with moderate impulses. Delivery falls from 88 to 65 percent, throughput to 74 percent of the control's. 375 of 762 bursts had an impulse on them and 175 were lost to it alone. Silences counted rise from 15 to 23.

| Measure | Total, 8 seeds | Range across seeds |
|---|---|---|
| Datagrams offered | 807 | 89 to 113 |
| Datagrams delivered | 527 | 56 to 79 |
| Datagrams dropped by the station | 0 | 0 to 0 |
| Datagrams lost on the air or still queued at the end | 280 | 30 to 45 |
| Useful throughput, bytes a second | 18.3 | 15.6 to 21.9 |
| Airtime at the slowest format, % | 23.4 | 16.2 to 32.0 |
| Airtime at the slow, under 1 kbit/s, % | 0.0 | 0.0 to 0.0 |
| Airtime at the middle, 1 to 10 kbit/s, % | 24.1 | 11.2 to 31.8 |
| Airtime at the fast, 10 kbit/s and over, % | 52.5 | 41.6 to 70.5 |
| Data bursts sent | 762 | 84 to 107 |
| Bursts not decoded | 268 | 28 to 40 |
| not decoded: path too weak for the format | 0 | 0 to 0 |
| not decoded: another station's burst (or its own transmission) overlapped | 92 | 5 to 16 |
| not decoded: fell in a fade | 0 | 0 to 0 |
| not decoded: an impulse overlapped (nothing else) | 175 | 16 to 26 |
| not decoded: none of these | 1 | 0 to 1 |
| Bursts an impulse overlapped, decoded or not | 375 | 33 to 61 |
| Times the station fell to the slowest format | 0 | 0 to 0 |
| of those, with no weak-signal cause | 0 | 0 to 0 |
| Silences counted | 23 | 0 to 5 |
| Addresses lost | 1 | 0 to 1 |

### What stands out

- **A good path is not loss free.** With nothing added, 12 to 19 percent of datagrams were not delivered (two stations 15 to 19 depending on the run, three stations 12, hidden 12). Nearly all of it is the stations' own bursts landing on each other, which is what the access faults already listed (#899 and the others) describe, plus a smaller share of bursts sent at a format the path could not hold (35 of 600 bursts in the two-station control). This is the floor every other figure sits on.
- **A steady loss of signal is handled well.** In the 12 dB step's window 28 percent of datagrams offered were not delivered against 16 percent in the control's, the station moved to the middle band and back, never fell to the slowest format, and throughput over the run was within 2 percent of the control's.
- **Impulses make the station slow down, and slowing down costs.** At moderate impulses the fast formats fall from 74 to 28 percent of airtime and throughput to 87 percent of the control's; at heavy, the fast formats are never used and throughput is 69 percent. The station falls to the slowest format rarely (1 time at moderate, 7 at heavy, 3 with three stations at moderate), but every one of those falls had a path that could have held the format it fell from. Those 11 falls are the count to drive to zero; they understate the effect, which is mostly a slide down through the middle band. Slower bursts are longer and are overlapped more: bursts lost to another station's burst go from 77 to 138 between the control and heavy impulses.
- **Impulses and sharing the channel together are the worst case.** Three stations with moderate impulses deliver 55 percent of datagrams and 63 percent of the control's throughput; silences counted rise from 8 to 55.
- **Slow flutter is the most damaging thing tried.** At 3 Hz, 43 of 59 bursts in the episode were lost and 16 of 61 datagrams offered then arrived (14 of 61 at the deeper fade). At 20 Hz and in the crossing about half arrived. The station did not fall to the slowest format in any flutter run; it moved some airtime to the middle band.
- **Results vary a good deal between seeds.** The share of airtime in the slow band at three stations with moderate impulses runs from 0 to 70 percent across seeds; at heavy impulses the share at the slowest format runs from 2 to 40. A comparison on one seed would have said almost anything.
- **A few bursts have no cause the simulator can name.** 0 to 7 a scenario, 37 across all 17; small, and not investigated here.
