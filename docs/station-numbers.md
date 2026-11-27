# Link addresses and station numbers: the design

Status: agreed by Tom on 2026-11-27 ("so effectively MAC addresses and ARP. OK"), for packet-net/pdn-soundmodem#835. Not yet built. It replaces the interim arrangement (the station number is a setting, the host binds names to numbers, a heard callsign becomes a number's name). Amended the same day after a review against a draft third-party network stack that would run over the modem (see "Amended" at the end). Two earlier forms of this document were dropped the same day: one kept names in the modem with a question and answer on the air, one had the host send to numbers. "Choices made here" at the end lists what was settled without asking.

## In one paragraph

The modem is a link layer with addresses of its own, as a LAN is. Each datagram has a **link address** it is going to and one it comes from: opaque bytes that the host chooses and the modem never interprets. The modem learns where an address lives from the "from" on each burst it hears, as an Ethernet switch does, and sends to everyone when it does not know yet. **Station numbers** are the modem's private, short, on-air form of "which station": it picks its own at random, the modems settle clashes among themselves, and no host ever sees one.

In LAN terms: a link address is a MAC address the host chooses, the modem is the learning switch, and a host protocol that needs to find a neighbour (IP's ARP) runs over it as over any LAN.

## Why this shape

A modem that picks a rate for each station must know, for each datagram, which station it is for. Three things were wanted and only two can hold at once: the modem knows nothing of the host's protocol; the host knows nothing of station numbers; the modem adds no addressing of its own. IP breaks the third whatever is done, because IP needs a link layer to give it addresses and a routed packet does not name its next hop. So the third went.

For AX.25 the link address is the same bytes as an address inside the frame, and saying it twice costs a few bytes a burst. That is the price of the modem not being an AX.25 modem. AX.25 already runs this way over other addressed links (AXUDP, Ethernet), where something maps callsigns to the link's own addresses, often by learning.

## What a station has

- **Link addresses**: whatever its host sends from. Any number of them. For AX.25: its callsigns and SSIDs, its aliases, and any callsign it transmits on behalf of.
- **A number**, 1 to 63, in every burst's header. The modem's own.
- **A station id**: 32 random bits, made up with the number and never stored. It has one use: noticing that a number is held by two stations or has changed hands.
- **The transmitter callsign**, given by the host. Sent in identification bursts and in Morse. Never an address.

A station with no identity has no number and no id. It listens, learns, and is never addressed.

## Link addresses

A link address is 1 to 32 bytes. "Everyone" is a separate value, not a string of bytes.

On the air an address travels as its **key**: four bytes, the payload's CRC-32 run over the address's bytes. Addresses stay opaque and of any length, and the cost does not depend on them. Two addresses with the same key would be confused; with three hundred addresses in hearing the chance that any two share a key is about one in a hundred thousand, and the cost is datagrams a host discards.

## On the air

Nothing changes in the header. Two blocks of tagged items carry what is new.

**In a data burst to one station**, the prelude's extension block gains an item:

| Tag | Item | Value |
|---|---|---|
| 1 | From | one or more runs of 6 bytes each: a key (4 bytes), a protocol byte and a count. The next count datagrams in the burst were sent from that address and are of that protocol. The runs are in the datagrams' order and their counts add up to the number of datagrams |

**The protocol byte** says what a datagram is, so that hosts of different protocols can share a channel (as the PID byte does in AX.25 and the EtherType on a LAN). Its values are borrowed from AX.25's PID field, which already has numbers for what is likely to be carried (0xCC IP, 0xCD ARP, 0xCF NET/ROM, 0xF0 no layer 3), with one added: 0x00 is a whole AX.25 frame. A host ignores datagrams of a protocol it does not speak.

**A data burst to everyone** now begins with a block of its own, in the same form as the extension block (one length byte, then items), before the datagrams. Until now it began with the datagrams.

| Tag | Item | Value |
|---|---|---|
| 1 | From | as above |
| 2 | For | one key: the link address the burst is really for. Present when the sender did not know where that address lives; absent in a burst that is for everyone |

**In an identification burst**, the items after the capability word gain:

| Tag | Item | Value |
|---|---|---|
| 1 | Station id | 4 bytes, in every identification burst |
| 2 | Clash | a number, 1 byte, and the station id that should move, 4 bytes |

From is sent in every data burst for now. At the slowest format a run costs about a fifth of a second. It is agreed that this becomes occasional (see "Amended"), as a later step.

## Learning where an address lives

Every station learns from every data burst it decodes, whoever the burst is for: each key in a From run now lives at the sender's number. The latest burst heard wins.

What a station knows about an address is forgotten:

- when the station it lives at is forgotten (unheard for ten minutes);
- when three bursts running to that station have met silence, which is when a sender already falls back to first contact;
- when that station's number changes hands (below).

After any of these the next datagram for the address goes to everyone again.

## Sending

The host gives a datagram, the address it is for, the address it is from and its protocol byte.

- **The address is known:** the datagram goes in a burst to that station, as now.
- **The address is not known:** it goes in a burst to everyone, at the first-contact format, with For set. This is the format a first contact uses anyway. There is no prelude, so no report is asked for and none comes back. The answer, when the far host sends one, carries its own From, and the two then address each other.
- **The address is everyone:** a burst to everyone with no For.

A burst to everyone carries datagrams for one For, or for none. Datagrams waiting for an address follow it when it becomes known or is forgotten.

## Receiving

A burst addressed to this station is delivered to the host, as now. A burst to everyone is delivered to the host whether or not it has a For. With each datagram the host is given the key of the address it was sent from, its protocol byte, which of the three ways it was addressed and, when there is one, the For key.

The From key is the datagram's layer 2 source, as a source MAC address is. A host whose own header does not say who sent a packet (one that uses short labels agreed between neighbours, say) cannot work without it.

The modem does not filter on For. A host that can tell from its own frame whether a datagram is for it (AX.25) ignores the key. One that cannot (an IP host, which would otherwise route a packet that was only passing) compares it with the key of its own address, which the library computes for it.

## Station numbers

**Taking one.** When the host gives the identity, the station picks at random among the numbers it has not heard in the last ten minutes, and makes up its station id. If it has heard all 63 it takes the one unheard longest. It does not wait and listen first.

**Two stations with one number, in hearing of each other.** A station that hears a burst with its own number as the sender, which it did not send, takes a new number and a new id at once. If both notice, both do.

**Two stations with one number, hidden from each other.** A third station finds out when it has heard identification bursts from one number with two different station ids inside ten minutes. It sends an identification burst of its own carrying Clash (the number, and the id heard more recently), by the ordinary access rules. A station that hears Clash with its own number and id takes a new number. A station that was about to send Clash for a number and hears one for it does not send its own. An identification burst sent for this reason does not move the identification clock.

**Nobody hears both.** Then nobody is affected.

**A new number is not announced.** To everyone else the old number goes quiet and a stranger appears. Its link addresses are learned again from its next burst, and its path is measured from first contact.

**A number changes hands.** A station that hears an identification burst from a number it knows, with a station id different from the one it had, forgets what it measured for that number and the addresses it had there, and drops anything queued for it.

## What the host sees

- **Send**(datagram, to, from, protocol, tag, priority, report wanted). The request to bind a name to a number goes, and the refusal "not bound".
- **Received**(datagram, the From key, the protocol byte, how it was addressed, the For key if any, quality). How it was addressed is one of: to this station, to everyone, or for an address not yet located.
- **LinkState**, **Congestion** and the snapshot are by link address, for the addresses the host has sent to. An address not yet located has a state of its own. Two addresses at one station give two entries. LinkState gives the rate in both directions: what this station sends to them at, and what it has told them the path from them supports. A routing layer that costs a link from its receiving end needs the second.
- **PathReport** and **BurstNotDecoded** name the link address the datagrams were sent to.
- **Identified**(callsign, capability word). The callsign names nothing.
- **The key of an address**, as a function, for a host that needs to compare, or to answer a datagram whose sender it knows only by key. Send takes a key in place of an address.
- No station number appears anywhere.

## Limits and what is not covered

- 63 stations in hearing of each other.
- Anyone can send from any address. That is the trust AX.25 and an unswitched LAN have.
- A datagram for an address not yet located is heard by everyone's host, at the slowest format.
- An audio repeater that plays a station's own bursts back to it would look like a clash. The modem is for simplex channels.

## Choices made here

Settled without being put to Tom, because each is internal or a starting value. Each is fixed in the design, not a setting.

1. An address is 1 to 32 bytes and travels as a 4-byte key, the payload's own CRC-32.
2. From goes in every data burst, for now.
3. The modem learns from every burst it decodes, not only those for it.
4. Three silences forget an address, the same count that falls back to first contact.
5. The modem does not filter on For; the host is given the key.
6. A station id of 32 random bits, new with every number.
7. A station picks its number at once, without listening first.
8. Whoever notices a clash moves. For hidden stations the more recent arrival is told to.
9. Nothing is carried across a change of number.
10. The protocol byte borrows AX.25's PID numbers, with 0x00 added for a whole AX.25 frame.

## Amended (Tom, 27 November 2026)

After reading a draft of a network stack by another amateur that is meant to run over any layer 2, this modem included. It fits: it wants neighbour addresses, unreliable datagrams of 256 bytes or more and a broadcast, and it costs its links from bit rate and delivery, which the modem can tell it. It showed two things missing, both now above, and settled three more.

1. **Each received datagram comes with the address it was sent from.** Built with step one.
2. **Each datagram has a protocol byte.** Built with step one.
3. **A burst for everyone goes at the best rate that every known neighbour can take**, not always at the slowest. A stack that keeps its routing in step by broadcasts cannot afford the slowest format for all of them. A datagram for an address not yet located still goes at the first-contact format, since its station may not have been heard at all. A later step.
4. **From becomes occasional.** The sender gives each of its addresses a short number, says which key the number stands for now and then, and marks each run with the number alone. A later step.
5. **LinkState gives the rate in both directions.** Built with step one.

Larger datagrams. One burst at the slowest format carries a little over 330 bytes, because the header counts at most 252 payload symbols, and the modem does not split. Nothing in the design stands in the way of splitting later: the item blocks can carry a fragment's place, the capability word can say a station understands it, there are spare burst types and a version field, and with the protocol byte a splitting layer can also sit above the modem as a protocol of its own.
