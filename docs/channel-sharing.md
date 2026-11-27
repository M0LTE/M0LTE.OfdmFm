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
