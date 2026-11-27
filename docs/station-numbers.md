# Station numbers: a design

Status: a proposal, rewritten 2026-11-27 after Tom's comments on the first form, for packet-net/pdn-soundmodem#835. Nothing here is built. It replaces the interim arrangement (the number is a setting, the host binds names to numbers, a heard callsign becomes a number's name). The first form had the modem keep names and carry a station's record across a change of number; both went as more than the problem needs. "Choices made here" at the end lists what was settled without asking.

## The shape

1. **The modem looks after station numbers.** It picks its own at random and the modems settle clashes among themselves. Nobody configures one.
2. **The host sends to numbers.** A datagram goes to a station number or to everyone. A received datagram comes with its sender's number.
3. **The modem knows no names.** Not callsigns, SSIDs or aliases. Which number is which of the host's addresses is the host's affair, and it learns it from its own traffic, the way a host learns hardware addresses on a LAN.
4. **A number may change, and nothing is carried over.** A station on a new number is a stranger and is learned again.

The one callsign the modem holds is the transmitter identifier, which the host gives it and which is never an address.

## How a host finds a station

It reads its own frames. Every datagram arrives with the number of the station that transmitted it, and the host knows from the frame who that was in its own terms. For AX.25 that is the last digipeater that has repeated, or the source if none has: the engine can tell, where the modem could not. The host keeps that as a table of its own.

To reach an address it has no number for, it sends to everyone. For AX.25 the first frame of a connection goes that way, at the slowest format, which is the format a first contact would use anyway. The answer arrives with a number on it, and from then on the two address each other.

A host must expect a number to go stale. It drops what it knew about a number when the modem says that station has gone, or that the number now belongs to a different station (below), and it corrects its table whenever it hears an address from a new number.

## Taking a number

When the host gives the identity, the station picks at random among the numbers it has not heard in the last ten minutes (the time after which a neighbour is forgotten). If it has heard all 63 it takes the one unheard longest. It does not wait and listen first: a clash is cheap to fix and waiting costs every start.

A station with no identity has no number. It listens and is never addressed.

## Telling two stations apart

Two stations may share a number for a while, and may share a callsign for good. So each station makes up a **station id** when it takes a number: 32 random bits, never stored. It is sent as an item in every identification burst (tag 1, four bytes) and has one use: noticing that a number is held by two stations, or has changed hands.

## When two stations have the same number

**They can hear each other.** A station that hears a burst with its own number as the sender, which it did not send, takes a new number at once. If both notice, both do.

**They cannot hear each other, and a third station hears both.** The third station finds out when it has heard identification bursts from one number with two different station ids. It sends an identification burst of its own with a clash item (tag 2: the number, one byte, and the id heard more recently, four bytes), by the ordinary access rules. A station that hears its own number and id in one takes a new number. Several stations may notice; one that hears a clash item for a number before sending its own does not send it.

**Nobody hears both.** Then nobody is affected.

Until a clash is settled, datagrams for that number reach both stations and each host discards what is not for it.

## A new number

The station picks again, as above, and makes a new station id. It announces nothing. To everyone else the old number has gone quiet and a stranger has appeared, and both are things the modem already handles: the old number is forgotten after ten minutes, and the stranger is measured from first contact, which takes a few bursts.

## When a number changes hands

A station that hears an identification burst from a number it knows, with a station id different from the one it had for that number, drops everything it had measured for that number and starts again. It tells its host as it would for a station that has gone, so the host forgets what it thought was at that number.

## What the host sees

- **Send** takes a station number, or everyone. The refusal "not bound" goes, and the request to bind a name to a number goes.
- **Received** carries the sender's number.
- **LinkState**, **PathReport**, **BurstNotDecoded**, **Congestion** and the snapshot are by number.
- **LinkState** says "gone" when a station is forgotten and when its number changes hands.
- **Identified** carries the number, the callsign and the capability word. The callsign names nothing.
- The host is not told its own number. It has no use for it.

## Limits and what is not covered

- 63 stations in hearing of each other.
- A host that sends to a stale number reaches the wrong station until one of the corrections above happens. The wrong host discards the frames, and the sender's own link layer times out as it would on a dead path.
- A first frame sent to everyone gets no report and no hint if it is lost.
- An audio repeater that plays a station's own bursts back to it would look like a clash. The modem is for simplex channels.

## Choices made here

Settled without being put to Tom, because each is internal or a starting value. Each is fixed in the design, not a setting.

1. A station id of 32 random bits, new with every number, used only to notice clashes and changes of hands.
2. The id and the clash notice ride in identification bursts as items. No new burst type, nothing new in the header.
3. A station picks its number at once, without listening first.
4. Whoever notices a clash moves. For hidden stations the more recent arrival is told to.
5. A number that changes hands is reported to the host as the old station gone.
