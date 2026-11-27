using System.Globalization;

namespace M0LTE.OfdmFm;

/// <summary>
/// Every sentence the station's trace can say, in one place, so that the words are the same wherever a line is made and a test can pin each shape. Each method takes what the line is about, already named
/// (the station fills in how a station or an address is shown: see docs/trace.md), and returns the text, a sentence fragment that starts with a capital and has no full stop. Plain ASCII.
/// Nothing here is called unless someone is listening.
/// </summary>
internal static class TraceText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- numbers ----

    /// <summary>A rate, kbit/s, without the unit: 0.25, 12.5, 70, 123.</summary>
    internal static string Rate(double kbit) => kbit switch
    {
        < 10 => Fixed(kbit, "0.##"),
        < 100 => Fixed(kbit, "0.#"),
        _ => Fixed(kbit, "0"),
    };

    /// <summary>A time or a length of time in seconds, without the unit: 0.35, 1.4, 11.5.</summary>
    internal static string Seconds(double seconds) => Fixed(seconds, "0.##");

    /// <summary>A moment on the station's clock, as the lines show it: 18.40.</summary>
    internal static string Moment(double seconds) => Fixed(seconds, "0.00");

    /// <summary>Decibels to the whole dB, without the unit.</summary>
    internal static string Db(double db) => Fixed(db, "0");

    /// <summary>Decibels to a tenth, without the unit.</summary>
    internal static string DbTenth(double db) => Fixed(db, "0.#");

    private static string Fixed(double value, string format)
    {
        // Rounds as the format does, and never shows "-0".
        string text = value.ToString(format, Inv);
        return text == "-0" || text == "-0.0" || text == "-0.00" ? text[1..] : text;
    }

    /// <summary>"no datagrams", "1 datagram", "3 datagrams".</summary>
    internal static string Datagrams(int count) => count switch
    {
        0 => "no datagrams",
        1 => "1 datagram",
        _ => count.ToString(Inv) + " datagrams",
    };

    private static string Signal(double db) => double.IsNaN(db) ? "" : ", signal " + Db(db) + " dB";

    /// <summary>"everyone for G4ABC-1": where a datagram for an address not yet located goes.</summary>
    internal static string EveryoneFor(string address) => "everyone for " + address;

    // ---- Flow: receiving ----

    /// <summary>A burst for this station was decoded.</summary>
    internal static string HeardForUs(string who, int count, double signalDb) => $"Heard {who}: {Datagrams(count)}{Signal(signalDb)}";

    /// <summary>A burst for everyone was decoded.</summary>
    internal static string HeardForEveryone(string who, int count, double signalDb) => $"Heard {who}: {Datagrams(count)} to everyone{Signal(signalDb)}";

    /// <summary>A burst for everyone, asking for one address, was decoded.</summary>
    internal static string HeardForEveryoneAsking(string who, int count, string address, double signalDb) => $"Heard {who}: {Datagrams(count)} to everyone for {address}{Signal(signalDb)}";

    /// <summary>A burst for another station was decoded.</summary>
    internal static string HeardForOther(string who, int count, string other, double signalDb) => $"Heard {who}: {Datagrams(count)} for {other}{Signal(signalDb)}";

    /// <summary>A burst was heard and did not decode.</summary>
    internal static string HeardNotDecoded(string who) => $"Heard {who} but could not decode it";

    /// <summary>A burst decoded and what it held could not be read.</summary>
    internal static string HeardUnreadable(string who) => $"Heard {who}: could not read what was in it";

    /// <summary>An identification burst was decoded.</summary>
    internal static string HeardIdentified(string who, string callsign, double signalDb) => $"Heard {who}: identified as {callsign}{Signal(signalDb)}";

    /// <summary>Another burst, or a change in the signal, came on top of a burst.</summary>
    internal static string LandedOn(string who) => $"Something else landed on the burst from {who}: not used to judge the path";

    /// <summary>What the path from a station now supports.</summary>
    internal static string PathGood(string who, double kbit, double? was) => was is double before
        ? $"Path from {who} is good for {Rate(kbit)} kbit/s (was {Rate(before)})"
        : $"Path from {who} is good for {Rate(kbit)} kbit/s";

    /// <summary>A station told this one what it can send at.</summary>
    internal static string ToldBy(string who, double kbit, bool failed) => failed
        ? $"Told by {who}: our burst was not decoded, we can send at {Rate(kbit)} kbit/s"
        : $"Told by {who}: we can send at {Rate(kbit)} kbit/s";

    // ---- Flow: telling ----

    /// <summary>This station told a station what it can send at.</summary>
    internal static string Told(string who, double kbit, bool failed) => failed
        ? $"Told {who} its burst was not decoded: it can send at {Rate(kbit)} kbit/s"
        : $"Told {who} it can send at {Rate(kbit)} kbit/s";

    /// <summary>This station answered for one of the host's addresses.</summary>
    internal static string ToldHere(string who, string address) => $"Told {who} that {address} is here";

    // ---- Flow: sending ----

    /// <summary>A datagram that would not fit one burst was refused.</summary>
    internal static string RefusedTooLarge(string dest, int length, int room, double kbit) => $"Refused a {length.ToString(Inv)} byte datagram for {dest}: one burst carries {room.ToString(Inv)} bytes at {Rate(kbit)} kbit/s";

    /// <summary>A datagram was refused because the queue was full.</summary>
    internal static string RefusedFull(string dest, double airtimeSeconds) => $"Refused a datagram for {dest}: queue full, {Seconds(airtimeSeconds)} s on air waiting";

    /// <summary>A datagram was refused because the station has no callsign.</summary>
    internal static string RefusedNoCallsign(string dest) => $"Refused a datagram for {dest}: no callsign set";

    /// <summary>Datagrams were dropped at the host's request.</summary>
    internal static string DroppedCancelled(string dest, int count) => $"Dropped {Datagrams(count)} for {dest}: cancelled by the host";

    /// <summary>A datagram no longer fitted one burst after the rate fell.</summary>
    internal static string DroppedNoLongerFits(string dest, double kbit) => $"Dropped a datagram for {dest}: no longer fits one burst at {Rate(kbit)} kbit/s";

    /// <summary>A burst went on air. <paramref name="dest"/> is an address, "everyone", or what <see cref="EveryoneFor"/> gives.</summary>
    internal static string Sent(int count, string dest, double kbit, double airtimeSeconds, bool reportAsked) =>
        $"Sent {Datagrams(count)} to {dest} at {Rate(kbit)} kbit/s, {Seconds(airtimeSeconds)} s on air{(reportAsked ? ", report asked for" : "")}";

    /// <summary>A burst that asked for a report was met with silence. <paramref name="silences"/> is the count, 1 to 3 or more.</summary>
    internal static string NoAnswer(string who, int silences) => silences switch
    {
        1 => $"No answer from {who} (1 of 3)",
        2 => $"No answer from {who} (2 of 3): sending slower",
        _ => $"No answer from {who} ({silences.ToString(Inv)} of 3): back to the slowest rate",
    };

    // ---- Flow: addresses ----

    /// <summary>An address the host has sent to was located.</summary>
    internal static string Found(string address) => $"Found {address}";

    /// <summary>An address that had been lost was located again.</summary>
    internal static string FoundAgain(string address) => $"Found {address} again";

    /// <summary>An address was heard from another station than the one it lived at.</summary>
    internal static string FoundMoved(string address, int now, int was) => $"Found {address} at station {now.ToString(Inv)}, was at station {was.ToString(Inv)}";

    /// <summary>Three silences running: the reason is the line before.</summary>
    internal static string LostAfterSilence(string address) => $"Lost track of {address}: sending to everyone until it is heard again";

    /// <summary>Ten minutes without a word.</summary>
    internal static string LostUnheard(string address) => $"Lost track of {address}: not heard for 10 minutes, sending to everyone until it is heard again";

    /// <summary>Its station number now belongs to another station.</summary>
    internal static string LostChangedHands(string address, int number) => $"Lost track of {address}: station number {number.ToString(Inv)} is now someone else, sending to everyone until it is heard again";

    // ---- Flow: the channel ----

    /// <summary>What is waiting to go: "2 datagrams to G4ABC-1".</summary>
    internal static string Waiting(int count, string dest) => $"{Datagrams(count)} to {dest}";

    /// <summary>What is waiting to go, for several addresses: "5 datagrams for 3 addresses".</summary>
    internal static string WaitingMany(int count, int addresses) => $"{Datagrams(count)} for {addresses.ToString(Inv)} addresses";

    /// <summary>The channel turned busy with something waiting to go.</summary>
    internal static string ChannelBusyWaiting(string waiting) => $"Channel busy: waiting to send {waiting}";

    /// <summary>The channel turned clear with something waiting to go.</summary>
    internal static string ChannelClearWaiting(string waiting) => $"Channel clear: waiting to send {waiting}";

    // ---- Flow: identity ----

    /// <summary>The host gave the station its callsign.</summary>
    internal static string CallsignSet(string callsign) => $"Callsign set to {callsign}";

    /// <summary>The station took its first number.</summary>
    internal static string TookNumber(int number) => $"Took station number {number.ToString(Inv)}";

    /// <summary>An identification burst went on air.</summary>
    internal static string SentIdentification(string callsign, double airtimeSeconds) => $"Sent identification as {callsign}, {Seconds(airtimeSeconds)} s on air";

    /// <summary>The station moved to another number because another station held its own.</summary>
    internal static string NumberChangedInUse(int was, int now) => $"Changed station number from {was.ToString(Inv)} to {now.ToString(Inv)}: {was.ToString(Inv)} is in use by another station";

    /// <summary>The station moved to another number because it was told to.</summary>
    internal static string NumberChangedTold(int was, int now) => $"Changed station number from {was.ToString(Inv)} to {now.ToString(Inv)}: told to move by another station";

    /// <summary>Two stations were found to use one number, and this station will say so.</summary>
    internal static string NumberClash(int number) => $"Station number {number.ToString(Inv)} is in use by two stations: will tell the later one to move";

    /// <summary>The same, for a station that has no callsign and so cannot say anything.</summary>
    internal static string NumberClashListening(int number) => $"Station number {number.ToString(Inv)} is in use by two stations: not telling, no callsign set";

    /// <summary>A station's number now belongs to another station, long after the first held it.</summary>
    internal static string NumberChangedHands(int number) => $"Station number {number.ToString(Inv)} changed hands: forgetting what was known of it";

    /// <summary>This station's notice that a number is in use by two stations went on air.</summary>
    internal static string ToldToMove(int number) => $"Told the station using number {number.ToString(Inv)} to move";

    /// <summary>A notice naming a station that must move was heard.</summary>
    internal static string HeardMoveNotice(int number) => $"Heard a notice that the station using number {number.ToString(Inv)} must move";

    // ---- Flow: decoding could not keep up ----

    /// <summary>A burst was dropped before decoding because too many were waiting.</summary>
    internal static string DroppedBurst(string who) => $"Dropped a burst from {who}: decoding could not keep up";

    /// <summary>A burst could not be decoded because the start of it had left the buffer.</summary>
    internal static string LostBurst(string who) => $"Lost a burst from {who}: its start had left the buffer";

    // ---- Detail ----

    /// <summary>What one burst did to the estimate of a path, with the format numbers and the rest of the internals in brackets.</summary>
    internal static string Measured(string who, double signalDb, double estimateDb, double marginDb, double kbit, int format, int measurement, bool openingOnly, int? pending, int? cap) =>
        $"Measured {who}{(openingOnly ? " from the opening only" : "")}: signal {Db(signalDb)} dB, estimate now {DbTenth(estimateDb)} dB, margin {DbTenth(marginDb)} dB, path good for {Rate(kbit)} kbit/s"
        + $" [format {format.ToString(Inv)}, measurement {measurement.ToString(Inv)}{(pending is int p ? $", faster format {p.ToString(Inv)} waits for a second burst" : "")}{(cap is int c ? $", held to format {c.ToString(Inv)} after a failure" : "")}]";

    /// <summary>The channel turned busy, and why.</summary>
    internal static string ChannelBusy(string cause) => $"Channel busy: {cause}";

    /// <summary>The channel turned clear with nothing waiting.</summary>
    internal static string ChannelClear() => "Channel clear";

    /// <summary>The cause of a busy channel, as a clause.</summary>
    internal static string CauseText(AccessCause cause) => cause switch
    {
        AccessCause.OwnTransmission => "this station is sending",
        AccessCause.Carrier => "the radio reports a carrier",
        AccessCause.HeardBurst => "another station is sending",
        AccessCause.Judging => "a signal is being checked",
        AccessCause.ReportHoldOff => "a report just came in, ordinary traffic waits a second",
        AccessCause.NumberChanged => "the station number changed, so it is built again",
        AccessCause.Refused => "the audio layer would not key it",
        _ => "unknown",
    };

    /// <summary>The host's carrier indication changed. <paramref name="state"/> is true, false, or null for none available.</summary>
    internal static string CarrierFromRadio(bool? state) => state switch
    {
        true => "Carrier from the radio: present",
        false => "Carrier from the radio: gone",
        _ => "Carrier from the radio: not available",
    };

    /// <summary>A request to transmit was made. <paramref name="wait"/> says what the rules ask of the channel first.</summary>
    internal static string AskedToSend(string subject, string wait) => $"Asked to send {subject}: {wait}";

    /// <summary>The wait for a report or an expedited datagram.</summary>
    internal static string WaitFirstSlot(double clearSeconds) => $"needs the channel clear for {Seconds(clearSeconds)} s";

    /// <summary>The wait for ordinary traffic.</summary>
    internal static string WaitOrdinary(double clearSeconds) => $"needs the channel clear for {Seconds(clearSeconds)} s plus a random wait";

    /// <summary>The random wait was drawn from slots.</summary>
    internal static string DrewWaitSlots(double seconds, long slot, long slots, double slotSeconds) => $"Drew a random wait of {Seconds(seconds)} s: slot {slot.ToString(Inv)} of {slots.ToString(Inv)}, {Seconds(slotSeconds)} s each";

    /// <summary>The random wait was drawn as one delay (the test rule).</summary>
    internal static string DrewWaitDelay(double seconds) => $"Drew a random wait of {Seconds(seconds)} s";

    /// <summary>When the head of the queue can go.</summary>
    internal static string CanSendAt(double moment) => $"Can send at {Moment(moment)} if the channel stays clear";

    /// <summary>A request was handed to the audio layer.</summary>
    internal static string ClearToSend(string subject, double waitedSeconds) => $"Clear to send {subject} after waiting {Seconds(waitedSeconds)} s";

    /// <summary>A request was taken back, and why.</summary>
    internal static string HeldBack(string subject, string cause) => $"Held back {subject}: {cause}";

    /// <summary>The host handed a datagram over.</summary>
    internal static string Queued(int length, string dest, int waiting) => $"Queued a {length.ToString(Inv)} byte datagram for {dest}: {waiting.ToString(Inv)} waiting";

    /// <summary>The rate and its reason, when a burst was built.</summary>
    internal static string Chose(double kbit, string dest, int format, string reason) => $"Chose {Rate(kbit)} kbit/s for {dest} [format {format.ToString(Inv)}]: {reason}";

    /// <summary>A report this station owes.</summary>
    internal static string OwingReport(string who, string reason) => $"Owing {who} a report: {reason}";

    /// <summary>A report that was owed and will not go alone.</summary>
    internal static string ReportCovered(string who) => $"Dropped the report owed to {who}: the datagrams going to it carry the news";

    /// <summary>An announcement that was owed and will not go alone.</summary>
    internal static string AnnouncementCovered(string address, string who) => $"No separate announcement of {address} to {who}: the datagrams going to it carry it";

    /// <summary>How long the station waits for an answer.</summary>
    internal static string AnswerWait(string who, double seconds) => $"Waiting up to {Seconds(seconds)} s for an answer from {who}";

    /// <summary>An answer came in time.</summary>
    internal static string AnswerInTime(string who) => $"Heard from {who} within the wait: not counted as silence";

    /// <summary>Why an identification is due.</summary>
    internal static string IdentificationDue(bool first) => first ? "Identification due: first transmission" : "Identification due: 10 minutes since the last";

    /// <summary>A header read that the station did nothing with.</summary>
    internal static string HeaderNotUsed(string reason) => $"Header read but not used: {reason}";

    /// <summary>A queue became full.</summary>
    internal static string QueueFull(string dest, double airtimeSeconds) => $"Queue for {dest} is full: {Seconds(airtimeSeconds)} s on air waiting";

    /// <summary>A full queue drained.</summary>
    internal static string QueueWritable(string dest) => $"Queue for {dest} can take datagrams again";

    /// <summary>A station was forgotten and none of the host's addresses lived there.</summary>
    internal static string ForgotStation(int number, string why) => $"Forgot station {number.ToString(Inv)}: {why}";

    /// <summary>A burst that was built carries the old number and will be built again.</summary>
    internal static string BuiltDiscarded(string what) => $"Built {what} discarded: it carries the old station number, building it again";

    /// <summary>The host was told a burst was not decoded.</summary>
    internal static string HostToldNotDecoded(string dest, int count) => $"Told the host that its burst to {dest} was not decoded: {Datagrams(count)}";

    /// <summary>Ordinary traffic is held off after a report.</summary>
    internal static string HoldingOrdinary(string who, double until) => $"Holding ordinary traffic until {Moment(until)}: report from {who}";

    /// <summary>The station sends its own data straight on after a lone report.</summary>
    internal static string GoingStraightOn(string who) => $"Going straight on to {who} after the report: no random wait";

    /// <summary>An owed notice that a number is in use by two stations was not needed.</summary>
    internal static string NoticeCancelled(int number) => $"Not sending a notice for number {number.ToString(Inv)}: another station sent it first";
}
