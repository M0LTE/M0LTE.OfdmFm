# M0LTE.OfdmFm

An adaptive OFDM modem for the audio path of an FM radio, for .NET.

Every burst names its own format in its header, so a receiver needs no setting to decode it. There are 256 standard formats, from 0.25 to about 70 kbit/s on a 25 kHz FM channel, and stations tell each other over the air what rate the path supports. The protocol is in [docs/adaptation-protocol.md](docs/adaptation-protocol.md).

The design of the live station, and what has been built of it so far, is in [docs/station-design.md](docs/station-design.md).

## Install

```sh
dotnet add package M0LTE.OfdmFm
```

## State

0.1.0 is the first release. While the version is 0.x the API will still change ([docs/versioning.md](docs/versioning.md)).

What is here:

- **The waveform.** Render and receive bursts in any of the 256 formats, pinned to a prototype by test vectors.
- **The rate rules.** What a station measures, what it recommends to a sender, and what it chooses to send at.
- **A live station** (`Station`). It takes a continuous audio stream and a host's datagrams and does the rest: finds and decodes bursts, decides when it may transmit, picks the rate for each neighbour, tells other stations what their path supports and hears the same back, identifies itself, picks its own on-air number, and learns where the host's link addresses live. It has no threads, no clock and no I/O of its own: a host pushes audio in and is called back.

How far it has been tried:

- The waveform and the rate rules ran between two real radios as a prototype, from a strong signal down to about -124 dBm.
- The live station has run only in simulation: two and three stations on a simulated FM channel, with a hidden station, interference part-way through a burst, a path cut and restored, and soaks of several minutes. Nothing is delivered twice, nothing is lost without the host being told, and no station gets stuck.
- Nothing has been measured on air with this library yet, and the simulated channel's model of what an FM receiver does when signals overlap is an assumption, not a measurement.

The design, and what each step found, is in [docs/station-design.md](docs/station-design.md); addressing is in [docs/station-numbers.md](docs/station-numbers.md). What a station did and why, line by line, for diagnosing a flow, is in [docs/trace.md](docs/trace.md).

Extracted from [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem); see [PROVENANCE.md](PROVENANCE.md).

| Type | What it is |
| --- | --- |
| `BurstRenderer` | Header and payload to 48 kHz audio samples. |
| `BurstFinder`, `HeaderReader` | Finds burst starts in a capture, then reads a burst's header. |
| `PayloadReceiver`, `TrackedReceiver` | Receive a burst's payload; the tracked one also follows a far station's sample clock through the burst. |
| `BurstHeader`, `BurstHeaderCodec`, `StandardFormats` | The header, its 51-bit form, and the format list. |
| `PayloadCodec` | Packs datagrams into a payload and unpacks them; packs and unpacks an identification burst's payload (callsign, capability word, items). |
| `LinkAdaptation`, `StandardFormats.Recommend` | What a station remembers about each station it hears, the format it recommends to each, and the format it chooses to send at; and which format a measured quality supports. A host drives it and passes the time in. |
| `Station` | The live station, one per radio channel: its sample clock, `Push` and `Skip`, reception (`RunOne`, `RunPending`, `DeliverResults`; events `Received`, `PathReport`, `LinkState`), the channel (`SetExternalBusy`, event `ChannelBusy`), the transmit request handshake with the audio layer (`TransmitStarted`, `TransmitEnded`, `TransmitRefused`), the count of bursts dropped from the decode queue (`BurstsDropped`), and the host's operations (`Send`, `Cancel`, `Snapshot`; events `Transmitted`, `Dropped`, `Congestion`), all by link address, and identification (`IdentificationDue`, `ModemSent`, `Identified`). One audio thread plus any number of worker threads; the thread rules are in the class's documentation. |
| `LinkAddress` (1 to 32 opaque bytes, and its 4-byte `Key`), `HostTag`, `SendPriority`, `SendResult`, `*Event` types | The host interface's argument, result and event types. |
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

A host sends a datagram through a station (inline, so the example needs no worker threads). The host gives the station its callsign first: a station with none receives and never transmits. The audio layer's part is the three calls that follow the request:

```csharp
var station = new Station(new Random(1), runJobsInline: true);
station.SetIdentity("M0LTE");                           // the callsign it identifies with; until it has one the station only listens. It picks its own station number then: the host never sees one
var peer = LinkAddress.FromAscii("G9XYZ");
var me = LinkAddress.FromAscii("M0LTE-1");              // an address of the host's own: datagrams are sent from it, and the station learns from it where this host is

long id = 0;
ReadOnlyMemory<float> audio = default;
station.TransmitRequest += r => (id, audio) = (r.Id, r.Samples);
station.Transmitted += e => Console.WriteLine($"tag {e.Tag.Value} went at {e.RateBitsPerSecond} bit/s");

SendResult accepted = station.Send(datagram, peer, me, LinkProtocol.NoLayer3, new HostTag(1));
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
