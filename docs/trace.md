# The trace

A station can say what it does and why, one short line at a time. It is for diagnosing a flow on the bench or on air: what was heard, what the path was judged good for, what was sent and told, what went unanswered, what was found and lost, and when the station may transmit.

The library writes the words, so every host prints the same ones. The host adds only where they go.

## Using it

```csharp
station.TraceLevel = TraceLevel.Detail;          // Flow until set
station.Trace += e => Console.WriteLine(e);      // "  12.34  Heard GB7RDG-1: 2 datagrams, signal 31 dB"
```

`e.Seconds` is the station's clock, `e.Level` says which level the line belongs to and `e.Text` is the sentence. `e.ToString()` writes the clock in a column seven wide, two spaces, and the text. The handler is called on the audio thread, as the other events are, and must not call the station.

Attach the handler before the host's first `Send`. An address the host sent from earlier is shown by its key until it is sent from again.

With no handler nothing is built or allocated for a line. With a handler at Flow, no Detail text is built.

## Levels

- **Flow**: a line or two a burst. Enough to follow a conversation.
- **Detail**: Flow, and each measurement, each choice of rate and every decision about when to transmit. The format numbers appear here, in brackets.

## Names

An address the host has sent to or from is shown as its bytes when they are printable ASCII (`G4ABC-1`). Otherwise, and for an address known only from the air, it is `address 3fa2c1d0`, the four-byte key in hex.

A station is named by an address it is known by. When it has several, the one the host used most recently is used, and an address the host has used ranks above one it has not. A station with no address known is `station 17`, its on-air number. Station numbers are hidden from the host interface; this view is the modem's own, so they may appear.

Lines about the host's own act (a datagram refused, queued or dropped) name the address the host used. Lines about a station (heard, told, sent to) name the station.

## Reading a trace

The first station of a two-station run: two datagrams sent to S2 (the burst at first contact carries one, so two bursts go), the path reported good for 70.4 kbit/s, S2's own datagram heard, and the next datagram sent at the new rate.

```
   5.80  Sent 1 datagram to S2 at 0.25 kbit/s, 2.07 s on air, report asked for
   7.97  Channel clear: waiting to send 1 datagram to S2
   8.77  Sent 1 datagram to S2 at 0.25 kbit/s, 2.07 s on air, report asked for
  11.57  Told by S2: we can send at 70.4 kbit/s
  12.82  Told by S2: we can send at 70.4 kbit/s
  20.78  Heard S2: 1 datagram, signal 52 dB
  20.78  Path from S2 is good for 70.4 kbit/s
  20.78  Told by S2: we can send at 70.4 kbit/s
  21.85  Told S2 it can send at 70.4 kbit/s
  40.00  Sent 1 datagram to S2 at 70.4 kbit/s, 0.35 s on air
```

## The lines

Each line is a sentence fragment that starts with a capital and has no full stop. The clock is the station's: seconds on its sample clock. A line says what happened, and after a colon why. The same words mean the same thing every time, so a line can be searched for. The examples use GB7RDG-1 and G4ABC-1 for addresses; a line names a station or an address as described above.

### Receiving

| Level | Line | When |
|---|---|---|
| Flow | `Heard GB7RDG-1: 2 datagrams, signal 31 dB` | A burst for this station decoded. The signal is the mean quality of the burst across its carriers; it is left out when none was measured. |
| Flow | `Heard GB7RDG-1: 1 datagram to everyone, signal 28 dB` | A burst for everyone decoded. |
| Flow | `Heard GB7RDG-1: 1 datagram to everyone for G4ABC-1, signal 28 dB` | A burst for everyone decoded that asks for one address: the sender had not located it. |
| Flow | `Heard GB7RDG-1: 3 datagrams for station 17, signal 40 dB` | A burst for another station decoded. Nothing goes to the host; what it says of where addresses live is learned. |
| Flow | `Heard GB7RDG-1: no datagrams, signal 31 dB` | A burst that carried only news (where an address lives, what the path supports). |
| Flow | `Heard station 9 but could not decode it` | A burst was found and its payload did not decode, whoever it was for. When it was for this station, a report goes back (see Telling). |
| Flow | `Heard station 9: could not read what was in it` | A burst decoded and what it held makes no sense. Nothing is delivered and nothing learned. |
| Flow | `Heard GB7RDG-1: identified as GB7RDG, signal 33 dB` | Another station's identification decoded. |
| Flow | `Something else landed on the burst from GB7RDG-1: not used to judge the path` | The signal collapsed part-way or another burst began inside it. It follows the line for the burst. |
| Flow | `Dropped a burst from GB7RDG-1: decoding could not keep up` | More than four bursts were waiting to be decoded and the oldest was thrown away. The computer is too slow for the traffic. |
| Flow | `Lost a burst from GB7RDG-1: its start had left the buffer` | A burst was known of too late to be copied. A defence; real audio does not reach it. |
| Detail | `Header read but not used: a version this station does not know` | A header read and nothing was done with it. The other reasons are "a kind of burst this station does not receive" and "it describes no burst". |

### Measuring

| Level | Line | When |
|---|---|---|
| Flow | `Path from GB7RDG-1 is good for 70.4 kbit/s (was 54.9)` | What the path from a station supports changed. The first time, there is no "was". |
| Detail | `Measured GB7RDG-1: signal 31 dB, estimate now 29.8 dB, margin 3 dB, path good for 70.4 kbit/s [format 31, measurement 5]` | A burst went into the estimate for the path. The brackets hold the format number, how many bursts have gone in, and, when they apply, "faster format 24 waits for a second burst" and "held to format 18 after a failure". "From the opening only" follows the name when the burst did not decode and only its opening was measured. |

### Telling

| Level | Line | When |
|---|---|---|
| Flow | `Told GB7RDG-1 it can send at 70.4 kbit/s` | A report or the prelude of a burst gave a station the rate its path supports. A burst that carries it says it only when it changed what the station was last told. |
| Flow | `Told GB7RDG-1 its burst was not decoded: it can send at 12.5 kbit/s` | A report that a burst from that station did not decode went on air. |
| Flow | `Told GB7RDG-1 that G4ABC-1 is here` | A station asked for one of the host's addresses and was answered. |
| Flow | `Told by GB7RDG-1: we can send at 70.4 kbit/s` | A station told this one what it can send at, alone or in a burst. |
| Flow | `Told by GB7RDG-1: our burst was not decoded, we can send at 12.5 kbit/s` | A station said it could not decode a burst of ours. The host is told too, and a Detail line says which datagrams. |
| Detail | `Owing GB7RDG-1 a report: its burst was not decoded` | A report is owed and will go when the channel allows. The other reasons are "the path from it got worse" and "it asked for one". |
| Detail | `Dropped the report owed to GB7RDG-1: the datagrams going to it carry the news` | A report was owed and data for the same station took the recommendation instead. |
| Detail | `No separate announcement of G4ABC-1 to GB7RDG-1: the datagrams going to it carry it` | The same for an announcement of an address. |
| Detail | `Told the host that its burst to G4ABC-1 was not decoded: 2 datagrams` | The host was given the hint that a burst was not decoded. |
| Detail | `Holding ordinary traffic until 12.26: report from GB7RDG-1` | A report for this station came in. Ordinary traffic waits a second from its end; the sender of the report is usually about to reply. |
| Detail | `Going straight on to GB7RDG-1 after the report: no random wait` | This station sent a lone report and has data for the same station. |

### Sending

| Level | Line | When |
|---|---|---|
| Flow | `Sent 3 datagrams to GB7RDG-1 at 70.4 kbit/s, 0.35 s on air, report asked for` | A burst went on air. The time on air counts the lead-in. "Report asked for" is added when the burst asked for one. |
| Flow | `Sent 1 datagram to everyone for G4ABC-1 at 0.25 kbit/s, 1.4 s on air` | A burst for an address not yet located: it goes to everyone, at the slowest rate, with no answer waited for. |
| Flow | `Sent 2 datagrams to everyone at 0.25 kbit/s, 2.07 s on air` | A burst for everyone. |
| Flow | `Refused a 400 byte datagram for G4ABC-1: one burst carries 333 bytes at 0.25 kbit/s` | The host sent a datagram bigger than one burst carries now. The station never splits a datagram. |
| Flow | `Refused a datagram for G4ABC-1: queue full, 30.4 s on air waiting` | The queue for the address holds 30 s of traffic. |
| Flow | `Refused a datagram for G4ABC-1: no callsign set` | The host sent before giving the station its callsign. |
| Flow | `Dropped 2 datagrams for G4ABC-1: cancelled by the host` | The host cancelled queued datagrams. |
| Flow | `Dropped a datagram for G4ABC-1: no longer fits one burst at 0.25 kbit/s` | The rate fell after the datagram was accepted and one burst no longer carries it. |
| Detail | `Queued a 30 byte datagram for G4ABC-1: 2 waiting` | The host handed over a datagram. The number is how many are waiting for that address. |
| Detail | `Chose 54.9 kbit/s for G4ABC-1 [format 29]: report said 70.4 kbit/s, 11.5 s ago` | The rate chosen for a burst, and why: "report said ...", "no report from them yet, going by our own estimate", "nothing known of them yet", "two silences running, two steps slower", "three silences running, first contact format", "address not located, first contact format" or "to everyone, first contact format". ", asking for a report" is added when the burst asks for one. |
| Detail | `Queue for G4ABC-1 is full: 30.2 s on air waiting` | The queue reached its limit. |
| Detail | `Queue for G4ABC-1 can take datagrams again` | A full queue drained below half. |

### Waiting for an answer

| Level | Line | When |
|---|---|---|
| Flow | `No answer from GB7RDG-1 (1 of 3)` | A burst that asked for a report was not answered within the wait. |
| Flow | `No answer from GB7RDG-1 (2 of 3): sending slower` | The second silence running. The next burst goes two steps slower. |
| Flow | `No answer from GB7RDG-1 (3 of 3): back to the slowest rate` | The third. The station falls back to first contact and forgets where its addresses live (see Addresses). |
| Detail | `Waiting up to 6.96 s for an answer from GB7RDG-1` | A burst that asked for a report ended. The wait is 3 s plus twice the burst's time on air. |
| Detail | `Heard from GB7RDG-1 within the wait: not counted as silence` | The wait ran out and something had been heard from the station in time. |

### Addresses

| Level | Line | When |
|---|---|---|
| Flow | `Found GB7RDG-1` | An address the host has sent to was heard from a station. For an address the host has not sent to, the same line is Detail. |
| Flow | `Found GB7RDG-1 again` | An address that had been lost was heard again. |
| Flow | `Found GB7RDG-1 at station 9, was at station 5` | An address was heard from a different station than the one it lived at. |
| Flow | `Lost track of GB7RDG-1: sending to everyone until it is heard again` | Three silences running. The line before says why. |
| Flow | `Lost track of GB7RDG-1: not heard for 10 minutes, sending to everyone until it is heard again` | Ten minutes without a word from the station it lived at. |
| Flow | `Lost track of GB7RDG-1: station number 17 is now someone else, sending to everyone until it is heard again` | The station number it lived at now belongs to another station. |
| Detail | `Forgot station 9: not heard for 10 minutes` | A station was forgotten and none of the host's addresses lived there. The other reasons are "no answer three times running" and "its number is now someone else". |

### The channel

| Level | Line | When |
|---|---|---|
| Flow | `Channel busy: waiting to send 2 datagrams to GB7RDG-1` | The channel turned busy with datagrams waiting for it (not when the station is itself sending). With several addresses waiting: "waiting to send 5 datagrams for 3 addresses". |
| Flow | `Channel clear: waiting to send 1 datagram to GB7RDG-1` | The channel turned clear and datagrams are still waiting out the clear time and the random wait. |
| Detail | `Channel busy: another station is sending` | The channel turned busy. The cause is one of "this station is sending", "the radio reports a carrier", "another station is sending" and "a signal is being checked". With datagrams waiting, the Flow line above comes first. |
| Detail | `Channel clear` | The channel turned clear with nothing waiting. |
| Detail | `Carrier from the radio: present` | The host's carrier indication changed. "gone" and "not available" are the other two. |

### When to transmit

| Level | Line | When |
|---|---|---|
| Detail | `Asked to send datagrams: needs the channel clear for 0.8 s plus a random wait` | A request to transmit was made: ordinary traffic. What is asked is datagrams, a report, an announcement, identification or a notice of a number in use by two stations. |
| Detail | `Asked to send a report: needs the channel clear for 0.05 s` | The same for a report, an identification or an expedited datagram: the first slot. |
| Detail | `Drew a random wait of 0.9 s: slot 2 of 2, 0.9 s each` | The random wait for ordinary traffic was drawn, afresh each time the channel was last busy. |
| Detail | `Can send at 18.40 if the channel stays clear` | When the request at the head of the queue can go. |
| Detail | `Clear to send datagrams after waiting 1.2 s` | The request was handed to the audio layer to key. |
| Detail | `Held back datagrams: another station is sending` | A request handed over and not keyed was taken back. The other causes are "the radio reports a carrier", "a signal is being checked", "a report just came in, ordinary traffic waits a second", "the station number changed, so it is built again" and "the audio layer would not key it". |

### Identity

| Level | Line | When |
|---|---|---|
| Flow | `Callsign set to M0LTE` | The host gave the station its callsign. |
| Flow | `Took station number 23` | The station took its on-air number. |
| Flow | `Sent identification as M0LTE, 2.07 s on air` | An identification burst went on air. |
| Detail | `Identification due: first transmission` | An identification fell due. The other reason is "10 minutes since the last". |

### Station numbers

| Level | Line | When |
|---|---|---|
| Flow | `Changed station number from 23 to 41: 23 is in use by another station` | The station heard a burst from its own number that it did not send. |
| Flow | `Changed station number from 23 to 41: told to move by another station` | A notice named this station. |
| Flow | `Station number 23 is in use by two stations: will tell the later one to move` | Two identifications with different ids came from one number within ten minutes. |
| Flow | `Station number 23 is in use by two stations: not telling, no callsign set` | The same for a station that cannot transmit. |
| Flow | `Station number 23 changed hands: forgetting what was known of it` | An identification with a new id came from a number after more than ten minutes. |
| Flow | `Told the station using number 23 to move` | The notice went on air. |
| Flow | `Heard a notice that the station using number 23 must move` | A notice from any station was decoded. |
| Detail | `Not sending a notice for number 23: another station sent it first` | A notice that was owed was cancelled because one was heard. |
| Detail | `Built burst discarded: it carries the old station number, building it again` | The station moved with a burst built and not yet on air. The other things it says are "report", "announcement", "identification" and "notice". |

## Things to know

- A burst's lines are made when its result is applied, which is a little after it ended (decoding takes time), so a burst heard at 20.35 can be reported at 20.78. The clock in a line is the clock when the station knew.
- A report that goes alone is said once, as it is built. A recommendation that rides on a data burst is said as the burst is built, and only when it differs from what the station was last told, so a steady flow does not repeat it.
- "Sent" is stamped when the burst goes on air and its time on air is the burst's length, lead-in included, as the host's `Transmitted` event counts it.
- Nothing is said twice. The reason a line has is in the line, or in the line directly before it (a third silence loses the address, and the line for the silence comes first).
