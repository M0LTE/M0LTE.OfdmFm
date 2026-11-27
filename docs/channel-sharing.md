# Sharing a channel: a design

Status: a proposal, written 2026-11-27, awaiting Tom's agreement. Nothing here is built. It answers packet-net/pdn-soundmodem#914 (tell interference from path loss) and the four faults the simulations found in the access rules (#899, #900, #901, #902), with #892 (the modem setting its own contention window) folded in. Prompted in part by Perry M0PYL's observation that when losses come from other stations and not from a weak path, slowing down is the wrong response.

## The problem in one paragraph

The modem was built around path loss. Two of its habits assume that a burst which fails, or goes unanswered, means the path got worse: the receiver widens its safety margin for that sender, and a sender that hears nothing three times running falls to the slowest format. When the real cause is another station transmitting at the same time, both habits make things worse: a slower burst is longer, and a longer burst is hit more often. The simulations show it. Three stations with traffic every few seconds collide on a third to a half of their bursts, and a link that collides slows down and then collides more.

## What a station can know

A station cannot see a collision directly, but it has evidence the usual rate-control schemes lack, because the receiver measures every burst:

- **The burst was fine and then was not.** Quality collapsed part-way through. Already handled: the estimate is left alone (the "sudden" judgement).
- **The burst's opening measured as well as ever, and the payload failed.** If a burst at that format, or a faster one, decoded from the same station within the last 30 s, the path has shown it carries that format. The failure is something landing on the payload.
- **Nothing came back, but the other station is still being heard, as strongly as before.** The path is not dead. The burst was most likely lost at the far end under something this station could not hear.
- **Nothing came back and nothing has been heard since.** The path may really have gone. This is the case the existing rules were written for, and they stay as they are for it.

## The rules proposed

### 1. Always draw a random wait before ordinary traffic

Today the random wait is drawn when the channel was last busy, so on a channel that has been idle a while it has already run out, and two stations given traffic at the same moment both key at once, every time (#899).

Proposed: the wait is drawn when a station has ordinary traffic ready and the channel is clear, every time. The cost is a short delay before each burst on a quiet channel: on average about a fifth of a second with a carrier indication, and about half a second to a second by detection alone.

### 2. The station just spoken to answers first

So that rule 1 does not slow a two-way exchange: the station a burst was addressed to may send to that sender in the first slot after it, whatever the traffic's class, with no random wait. Everyone else waits out the first slot and then draws. Reports and acknowledgements already work this way; this extends it to any reply. Two stations exchanging data then alternate with no random delay at all, while a third station defers to them.

### 3. A failure with a good opening, at a format known to work, is not the path's fault

When a payload fails, its opening measured within 2 dB of the estimate, and a burst at that format or faster decoded from the same station in the last 30 s: the receiver reports the failure, so the sender's host can resend, but leaves its margin and its cap alone. Otherwise as now.

### 4. Silence from a station that is still being heard means try again, not slow down

When a burst that asked for a report gets none, and the sender has heard the other station since, at about the strength it had before: the sender keeps its format and sends again after a wider random wait (rule 5). After three such tries in a row with nothing coming back, the existing rule takes over and it steps down. Where nothing has been heard from the station at all, the existing rule applies from the first silence.

With this, only a report or a burst addressed to this station counts as an answer. Hearing the other station talk to someone else shows it is alive, which matters for the paragraph above, but does not answer the question (#901).

### 5. The random wait widens when bursts are being lost to other stations

Each time a station puts a loss down to another station (a failed report for a burst sent, under rule 3's conditions as the sender sees them; a silence under rule 4; a "sudden" failure reported back), it doubles the number of slots it draws from, up to eight times the starting number. A burst that gets through resets it. This is the ordinary remedy for a shared channel, and it is what #892 asked for: the window sets itself from what the channel is doing.

### 6. Shorter bursts while losses are being put down to other stations

While the window of rule 5 is wider than its starting size, a burst carries at most one second of airtime, where the usual ceiling is 2.5 s (one datagram that needs longer still goes whole). Less on the air at once means less to lose and less to hit.

### 7. Do not start over the answer that was asked for

A station that asked for a report holds its next ordinary burst to that station until the report arrives or the time allowed for it has passed (1.5 s from the end of the burst). Today it may start again just as the report keys, and each destroys the other (#902).

## Left for later

- **A stronger burst that begins inside one already being received** (#900) is still thrown away unread. Reading it needs care not to bring back the false starts the present rule prevents. Its own piece of work.
- **What the layer above knows** (that a datagram is a resend, or was acknowledged): not needed by these rules. Revisit if the evidence above proves too thin.
- **A host naming the format** (#915): unchanged.
- **How busy the channel is** (#910) as further evidence: once it exists.
- **Going faster than recommended** when losses are collisions: the rules above stop the slowing down; whether to go further waits on results.

## How it will be judged

In the three-station simulation and the soak, over eight seeds each, with and without a carrier indication, and with a hidden station, comparing before and after: datagrams delivered, airtime used, collided bursts, and the rate each link holds. The claim to test is that a busy channel no longer drags its links to the slowest format, and that a quiet two-station link is no slower than it is today. Then on air, which needs a third radio.

## Choices made here

Settled without being put to Tom, because each is internal or a starting value. Each is fixed in the design, not a setting.

1. "Known to work" means a decode at that format or faster from the same station within the 30 s the rules already use for freshness.
2. "As strong as before" means within the 2 dB the rules already use for a drop.
3. Three tries at the same format before stepping down.
4. The window doubles up to eight times its starting size.
5. One second of airtime under contention.
