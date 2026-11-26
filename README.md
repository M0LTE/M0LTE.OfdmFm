# M0LTE.OfdmFm

An adaptive OFDM modem for the audio path of an FM radio, for .NET.

Every burst names its own format in its header, so a receiver needs no setting to decode it. There are 256 standard formats, from 0.25 to about 70 kbit/s on a 25 kHz FM channel, and stations tell each other over the air what rate the path supports. The protocol is in [docs/adaptation-protocol.md](docs/adaptation-protocol.md).

A design draft for the live station, which does not exist yet, is in [docs/station-design.md](docs/station-design.md).

## State

The transmit side, the receive arithmetic and the adaptation rules (what a station remembers, recommends and chooses) are complete, and are pinned to a prototype by test vectors. There is no live streaming receiver yet: the receivers here take a finished capture of a burst, not a continuous audio stream. The live `Station` exists as a skeleton (step 1 of the design): a sample clock, timers on it and the transmit handshake with the audio layer, with the host interface's event types declared; it does not yet receive or send. It has not been published to NuGet.

Extracted from [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem); see [PROVENANCE.md](PROVENANCE.md).

| Type | What it is |
| --- | --- |
| `BurstRenderer` | Header and payload to 48 kHz audio samples. |
| `BurstFinder`, `HeaderReader` | Finds burst starts in a capture, then reads a burst's header. |
| `PayloadReceiver`, `TrackedReceiver` | Receive a burst's payload; the tracked one also follows a far station's sample clock through the burst. |
| `BurstHeader`, `BurstHeaderCodec`, `StandardFormats` | The header, its 51-bit form, and the format list. |
| `PayloadCodec` | Packs datagrams into a payload and unpacks them. |
| `LinkAdaptation`, `StandardFormats.Recommend` | What a station remembers about each station it hears, the format it recommends to each, and the format it chooses to send at; and which format a measured quality supports. A host drives it and passes the time in. |
| `Station` | The live station, one per radio channel: so far its sample clock, `Push` and `Skip`, and the transmit request handshake with the audio layer. It does not yet receive or send. Not thread-safe. |
| `StationName`, `HostTag`, `SendPriority`, `*Event` types | The host interface's argument and event types, declared for the station. |
| `Interleaver`, `Constellation`, `Demapper` | The pieces of the chain, usable on their own. |

## Build and test

Needs the .NET 10 SDK.

```sh
dotnet test
```

The long tier of the tracked-receiver tests is off by default; set `SM_MASK_GATE=1` to run it.

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

The modem works at 48 kHz. This example runs as a test, `ReadmeExampleTests`.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE) and [PROVENANCE.md](PROVENANCE.md).
