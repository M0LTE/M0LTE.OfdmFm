# Station numbers and names: a design

Status: a proposal, written 2026-11-27 for packet-net/pdn-soundmodem#835. Nothing here is built. It replaces the interim arrangement (the number is a setting, the host binds names to numbers, a heard callsign becomes a number's name). The direction is Tom's, recorded in pdn-soundmodem's `docs/dev/ofdm-fm/host-interface.md`, amendments 7 to 9; the mechanism below is proposed and awaits his agreement. "Choices made here" at the end lists what was settled without asking.

## What was asked for

1. The modem is responsible for station numbers. The host never sees one.
2. A number is random, and clashes are settled by the modems.
3. A number may change from time to time, and that must not matter.
4. The transmitter identifier callsign is not an address: several stations may use the same one.
5. A host may send and receive under many names (callsigns and SSIDs, for AX.25). None of them can be read from traffic.

## Four things a station has

- **A number**, 1 to 63, in every burst's header. Its address for now.
- **A station id**: 32 random bits, made up when the station is given its identity and never stored. It is what stays the same when the number changes, and what tells two stations apart when they share a number or a callsign. A restart makes a new one, and the station is then a stranger, which is right: it has forgotten everything too.
- **The transmitter callsign**, from the host. Sent in identification bursts and in Morse. Never used to address or to tell stations apart.
- **Names**: the strings of bytes its host answers to. The host tells the modem which (a new request, below).

A station with no identity has none of these. It listens and is never addressed.

## On the air

Nothing new in the header, which has no room. Everything rides in the identification burst (burst type 2, to everyone, first-contact format), as items after the callsign and capability word. An identification burst sent for one of these reasons is the same burst as one sent because the ten-minute rule fell due, with more items. It does not move the identification clock, so the burst and the Morse ident stay on one rule.

| Tag | Item | Value | Sent |
|---|---|---|---|
| 1 | Station id | 4 bytes | in every identification burst |
| 2 | Looking for | one or more name keys, 4 bytes each | to ask which station answers to a name |
| 3 | Answers to | one or more name keys | in reply to a Looking for that matches one of this station's names, and only the keys that matched |
| 4 | Moved | the old number, 1 byte | with the first identification after a change of number |
| 5 | Clash | a number, 1 byte, and the station id that should move, 4 bytes | by a third station that has heard two ids on one number |

A **name key** is the CRC-32 of the name's bytes. Names stay opaque and of any length, and an item's size does not depend on them. Two names with the same key would be confused. With three hundred names in hearing the chance that any two share a key is about one in a hundred thousand, and the cost is datagrams a host discards.

At the first-contact format each item costs roughly a fifth of a second. An ask and its answer are two bursts of a little over a second each.

## Taking a number

When the host gives the identity, the station picks at random among the numbers it has not heard in the last ten minutes (the time after which a neighbour is forgotten). If it has heard all 63, it takes the one unheard longest. It does not wait and listen first: a clash is cheap to fix and waiting costs every start.

## Finding the station for a name

The host sends to a name. If the modem knows which station answers to it, the datagram goes. If not:

1. The datagram is queued and the station sends an identification burst with Looking for (the name's key), by the ordinary access rules.
2. The station that answers to it replies with an identification burst with Answers to (that key), in the first slot, as a report would go.
3. The asker binds the name to that station (its id, at its present number) and sends.

Everyone who hears an answer learns from it. If nothing comes back the ask is repeated, three times in all, each after the usual answer wait. Then the queued datagrams are dropped with a reason of their own, and for 30 seconds further sends to that name are refused at once.

A binding lasts as long as the station is remembered. If two stations answer to one name, the last one heard has it.

The reply path is found the same way: the far host answers to a name, and its modem asks. That is a second exchange on first contact, a few seconds at the slowest format. Having the asker volunteer its own names would save it, at the cost of guessing which of them matters; left out until it is shown to hurt.

## When two stations have the same number

**They can hear each other.** A station that hears a burst with its own number as the sender, which it did not send, moves at once. If both notice, both move. Nobody negotiates.

**They cannot hear each other, and a third station hears both.** The third station finds out when it has heard identification bursts from one number with two different station ids. It sends a Clash naming the number and the id heard more recently (the newcomer), by the ordinary access rules, and drops what it had measured for that number, since two paths were mixed in it. A station that hears a Clash with its own number and id moves. One that hears its number with another id stays. Several stations may notice; one that hears a Clash for a number before sending its own does not send it.

Until a clash is settled, datagrams for that number reach both stations and each host discards what is not for it.

**Nobody hears both.** Then nobody is affected.

## Moving

The station picks a new number as above and sends an identification burst with Moved (its old number). Whoever hears it moves that station's record, with its measurements and its names, to the new number, matching on the station id.

Whoever misses it goes on sending to the old number and hears nothing. No special case covers that. The existing rule does: after three silences a sender falls back to first contact, and from now on it also forgets which number the name had, so its next datagram begins with an ask. The answer carries the station id, the record is found again, and only the number has changed. The same path covers a station that restarted.

## What the host sees

- **New request: the names it answers to.** Add a name, remove a name. The AX.25 engine gives its callsigns, SSIDs and aliases. A host that gives none can send and can hear traffic for everyone, but no station can find it.
- **The request to bind a name to a number goes**, and with it the refusal "not bound".
- **Send to a name not yet found** is accepted and waits. If the station is not found it is `Dropped`, with a new reason, and sends for the next 30 seconds are refused with a new result.
- **`Identified`** carries the callsign and capability word and nothing else. A heard callsign no longer names anything.
- **`Received`** names its sender only if the host has itself sent to that station, by the name it last used. Otherwise none: the host reads its own frame, as it must anyway.
- **`LinkState`** and the snapshot are by the names the host has sent to. Two names for one station give two entries.

## Limits and what is not covered

- 63 stations in hearing of each other.
- Anyone can answer to any name. That is the trust AX.25 has today.
- An audio repeater that plays a station's own bursts back to it would look like a clash. The modem is for simplex channels.
- Stations that share a number and are never both heard by anyone go unnoticed, and it does not matter.

## Choices made here

Settled without being put to Tom, because each is internal or a starting value. Each is fixed in the design, not a setting.

1. A station id of 32 random bits, fresh at each start.
2. Everything rides in identification bursts as items. No new burst type.
3. A name goes on the air as a 4-byte key, not in full.
4. A station picks its number at once, without listening first.
5. Whoever notices a clash moves. For hidden stations the newcomer is told to.
6. Names are found on demand, by asking, not announced routinely.
7. Three asks, then 30 seconds of refusal.
8. No grace period on the old number after a move: the silence rule and a fresh ask recover it.
