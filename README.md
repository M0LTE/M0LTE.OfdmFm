# M0LTE.OfdmFm

An adaptive OFDM modem for the audio path of an FM radio, for .NET.

Every burst names its own format in its header, so a receiver needs no setting to decode it. There are 256 standard formats, from 0.25 to about 70 kbit/s on a 25 kHz FM channel, and stations tell each other over the air what rate the path supports. The protocol is in [docs/adaptation-protocol.md](docs/adaptation-protocol.md).

The design of the live station, and what has been built of it so far, is in [docs/station-design.md](docs/station-design.md).

## State

The transmit side, the receive arithmetic and the adaptation rules (what a station remembers, recommends and chooses) are complete, and are pinned to a prototype by test vectors. The receivers (`HeaderReader`, `PayloadReceiver`, `TrackedReceiver`) take a finished capture of one burst. The live `Station` (steps 1 to 6 of the design) takes a continuous audio stream: it keeps a 16 s ring, finds bursts with a streaming detector, reads headers and receives payloads as jobs that the caller runs inline or on its own worker threads, and delivers datagrams, path reports and link state, driving the adaptation rules in the order the bursts began and on the sample clock, whatever the decode speed. It knows when the channel is busy (a burst it has found, a candidate it cannot read, its own transmission, the host's carrier indication) and when it may transmit, and takes transmission requests that it grants or withdraws by those rules. And it sends: the host gives it datagrams (`Send`, `Cancel`, `Bind`, `Snapshot`), it queues them for each station, chooses a format by the adaptation rules once the channel is free, packs and renders a burst, and tells the host when each datagram has gone (`Transmitted`). Two stations in one test process exchange datagrams through a simulated channel (in the test project). The stations tell each other what the path supports: a station that could not decode a burst addressed to it says so at once in a lone report, one whose recommendation for a sender has dropped says so in the first slot, one that was asked for a report sends it with the next data or alone after 1.5 s, and the host is told (`BurstNotDecoded`) which datagrams were in a burst that was heard and not decoded. One-way traffic therefore climbs, and a 10 dB drop costs one burst. It does not yet identify itself (step 7). It has not been published to NuGet.

Extracted from [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem); see [PROVENANCE.md](PROVENANCE.md).

| Type | What it is |
| --- | --- |
| `BurstRenderer` | Header and payload to 48 kHz audio samples. |
| `BurstFinder`, `HeaderReader` | Finds burst starts in a capture, then reads a burst's header. |
| `PayloadReceiver`, `TrackedReceiver` | Receive a burst's payload; the tracked one also follows a far station's sample clock through the burst. |
| `BurstHeader`, `BurstHeaderCodec`, `StandardFormats` | The header, its 51-bit form, and the format list. |
| `PayloadCodec` | Packs datagrams into a payload and unpacks them. |
| `LinkAdaptation`, `StandardFormats.Recommend` | What a station remembers about each station it hears, the format it recommends to each, and the format it chooses to send at; and which format a measured quality supports. A host drives it and passes the time in. |
| `Station` | The live station, one per radio channel: its sample clock, `Push` and `Skip`, reception (`RunOne`, `RunPending`, `DeliverResults`; events `Received`, `PathReport`, `LinkState`), the channel (`SetExternalBusy`, event `ChannelBusy`), the transmit request handshake with the audio layer, and the host's operations (`Send`, `Cancel`, `Bind`, `Snapshot`; events `Transmitted`, `Dropped`, `Congestion`). One audio thread plus any number of worker threads; the thread rules are in the class's documentation. |
| `StationName`, `HostTag`, `SendPriority`, `SendResult`, `*Event` types | The host interface's argument, result and event types. |
| `Interleaver`, `Constellation`, `Demapper` | The pieces of the chain, usable on their own. |

## Build and test

Needs the .NET 10 SDK.

```sh
dotnet test
```

The long tier of the tracked-receiver tests is off by default; set `SM_MASK_GATE=1` to run it.

To time the tracked receiver on a dense burst and on the longest low-rate burst, run `SM_BENCH=1 dotnet run -c Release --project tests/M0LTE.OfdmFm.Tests -- -class M0LTE.OfdmFm.Tests.TrackedReceiverBenchmark`; it prints figures and asserts none. `SM_DUMP=<file>` (with `SM_DUMP_FULL=1` for the long synthetic bursts) writes everything the tracked receiver finds, bit for bit, so two builds can be compared.

## Example

```csharp
using M0LTE.OfdmFm;

// Transmit: pack a datagram, pick the shortest burst of a format that carries it, render it.
int format = StandardFormats.MostRobust;
byte[] payload = new byte[256];
int length = PayloadCodec.Pack(null, new[] { new ReadOnlyMemory<byte>(datagram) }, payload);
int symbols = PayloadCodec.SymbolsFor(format, length)!.Value;
var header = BurstHeader.Data(sender: 1, destination: 0, format, symbols, burstNumber: 0);
var renderer = new BurstRenderer();
float[] audio = new float[renderer.SamplesFor(format, symbols)];
renderer.RenderData(header, payload.AsSpan(0, length), audio);

// Receive: find the burst, read its header, receive the payload.
FormatOpening opening = StandardFormats.Describe(format).Opening;
BurstCandidate found = BurstFinder.Find(audio, opening).Single();
var headerReader = new HeaderReader();
headerReader.TryRead(audio, found.Index, opening, out BurstHeader read);
var receiver = new PayloadReceiver();
byte[] received = new byte[PayloadReceiver.PayloadRoom(read)];
PayloadReceiveResult result = receiver.Receive(audio, found.Index, read, headerReader.Bits, received);
PayloadCodec.TryUnpack(received.AsSpan(0, result.PayloadBytes), toEveryone: true, out _, out DatagramReader datagrams);
```

A host sends a datagram through a station (inline, so the example needs no worker threads). The audio layer's part is the three calls that follow the request:

```csharp
var station = new Station("M0LTE", 1, new Random(1), runJobsInline: true);
var peer = StationName.FromAscii("G9XYZ");
station.Bind(peer, 2);                                  // the interim way a name gets an on-air number

long id = 0;
ReadOnlyMemory<float> audio = default;
station.TransmitRequest += r => (id, audio) = (r.Id, r.Samples);
station.Transmitted += e => Console.WriteLine($"tag {e.Tag.Value} went at {e.RateBitsPerSecond} bit/s");

SendResult accepted = station.Send(datagram, peer, new HostTag(1));
float[] input = new float[4800];                        // the radio's audio, 0.1 s at a time (silence here)
while (id == 0) station.Push(input);                    // the station listens for 5 s first, then asks to transmit
station.TransmitStarted(id, station.SampleCount);       // play audio.Span to the radio, and say when it began ...
for (long left = audio.Length; left > 0; left -= input.Length)
{
    station.Push(input.AsSpan(0, (int)Math.Min(left, input.Length)));
}

station.TransmitEnded(id, station.SampleCount);         // ... and when it ended: Transmitted is raised here
```

The modem works at 48 kHz. These examples run as tests, `ReadmeExampleTests`.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE) and [PROVENANCE.md](PROVENANCE.md).
