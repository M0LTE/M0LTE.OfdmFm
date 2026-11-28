using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The catalogue in docs/trace.md and the words the library says are the same: each example in a table is what <see cref="TraceText"/> makes for it, at the level the table gives; every method of <see cref="TraceText"/> that makes a line has
/// an example in the document (so a new line cannot be added without its entry); and every fixed reason, kind and cause the lines use is listed word for word.
/// </summary>
public class TraceCatalogueTests
{
    private static string Doc([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "trace.md"));

    /// <summary>What the library says for the example of each row of the catalogue: the method, the level the row gives and the text, in the order of the document.</summary>
    private static (string Method, TraceLevel Level, string Text)[] Said() =>
        [
            (nameof(TraceText.HeardForUs), TraceLevel.Flow, TraceText.HeardForUs("GB7RDG-1", 2, 31.2)),
            (nameof(TraceText.HeardForEveryone), TraceLevel.Flow, TraceText.HeardForEveryone("GB7RDG-1", 1, 28)),
            (nameof(TraceText.HeardForEveryoneAsking), TraceLevel.Flow, TraceText.HeardForEveryoneAsking("GB7RDG-1", 1, "G4ABC-1", 28)),
            (nameof(TraceText.HeardForOther), TraceLevel.Flow, TraceText.HeardForOther("GB7RDG-1", 3, "station 17", 40)),
            (nameof(TraceText.HeardForUs), TraceLevel.Flow, TraceText.HeardForUs("GB7RDG-1", 0, 31.2)),
            (nameof(TraceText.HeardNotDecoded), TraceLevel.Flow, TraceText.HeardNotDecoded("station 9")),
            (nameof(TraceText.HeardUnreadable), TraceLevel.Flow, TraceText.HeardUnreadable("station 9")),
            (nameof(TraceText.HeardIdentified), TraceLevel.Flow, TraceText.HeardIdentified("GB7RDG-1", "GB7RDG", 33)),
            (nameof(TraceText.LandedOn), TraceLevel.Flow, TraceText.LandedOn("GB7RDG-1")),
            (nameof(TraceText.DroppedBurst), TraceLevel.Flow, TraceText.DroppedBurst("GB7RDG-1")),
            (nameof(TraceText.LostBurst), TraceLevel.Flow, TraceText.LostBurst("GB7RDG-1")),
            (nameof(TraceText.HeaderNotUsed), TraceLevel.Detail, TraceText.HeaderNotUsed(TraceWords.HeaderVersion)),
            (nameof(TraceText.PathGood), TraceLevel.Flow, TraceText.PathGood("GB7RDG-1", 70.4, 54.9)),
            (nameof(TraceText.Measured), TraceLevel.Detail, TraceText.Measured("GB7RDG-1", 31.2, 29.84, 3.0, 70.4, 31, 5, false, null, null)),
            (nameof(TraceText.Told), TraceLevel.Flow, TraceText.Told("GB7RDG-1", 70.4, false)),
            (nameof(TraceText.Told), TraceLevel.Flow, TraceText.Told("GB7RDG-1", 12.5, true)),
            (nameof(TraceText.ToldHere), TraceLevel.Flow, TraceText.ToldHere("GB7RDG-1", "G4ABC-1")),
            (nameof(TraceText.ToldBy), TraceLevel.Flow, TraceText.ToldBy("GB7RDG-1", 70.4, false)),
            (nameof(TraceText.ToldBy), TraceLevel.Flow, TraceText.ToldBy("GB7RDG-1", 12.5, true)),
            (nameof(TraceText.OwingReport), TraceLevel.Detail, TraceText.OwingReport("GB7RDG-1", TraceWords.OweFailed)),
            (nameof(TraceText.ReportCovered), TraceLevel.Detail, TraceText.ReportCovered("GB7RDG-1")),
            (nameof(TraceText.AnnouncementCovered), TraceLevel.Detail, TraceText.AnnouncementCovered("G4ABC-1", "GB7RDG-1")),
            (nameof(TraceText.HostToldNotDecoded), TraceLevel.Detail, TraceText.HostToldNotDecoded("G4ABC-1", 2)),
            (nameof(TraceText.HoldingOrdinary), TraceLevel.Detail, TraceText.HoldingOrdinary("GB7RDG-1", 12.26)),
            (nameof(TraceText.GoingStraightOn), TraceLevel.Detail, TraceText.GoingStraightOn("GB7RDG-1")),
            (nameof(TraceText.GoingStraightOnAfterIdentification), TraceLevel.Detail, TraceText.GoingStraightOnAfterIdentification()),
            (nameof(TraceText.HoldingForIdentification), TraceLevel.Detail, TraceText.HoldingForIdentification("GB7RDG-1", 14.62)),
            (nameof(TraceText.Sent), TraceLevel.Flow, TraceText.Sent(3, "GB7RDG-1", 70.4, 0.35, true)),
            (nameof(TraceText.Sent), TraceLevel.Flow, TraceText.Sent(1, TraceText.EveryoneFor("G4ABC-1"), 0.25, 1.4, false)),
            (nameof(TraceText.Sent), TraceLevel.Flow, TraceText.Sent(2, "everyone", 0.25, 2.07, false)),
            (nameof(TraceText.RefusedTooLarge), TraceLevel.Flow, TraceText.RefusedTooLarge("G4ABC-1", 400, 333, 0.25)),
            (nameof(TraceText.RefusedFull), TraceLevel.Flow, TraceText.RefusedFull("G4ABC-1", 30.4)),
            (nameof(TraceText.RefusedNoCallsign), TraceLevel.Flow, TraceText.RefusedNoCallsign("G4ABC-1")),
            (nameof(TraceText.DroppedCancelled), TraceLevel.Flow, TraceText.DroppedCancelled("G4ABC-1", 2)),
            (nameof(TraceText.DroppedNoLongerFits), TraceLevel.Flow, TraceText.DroppedNoLongerFits("G4ABC-1", 0.25)),
            (nameof(TraceText.Queued), TraceLevel.Detail, TraceText.Queued(30, "G4ABC-1", 2)),
            (nameof(TraceText.Chose), TraceLevel.Detail, TraceText.Chose(54.9, "G4ABC-1", 29, "report said 70.4 kbit/s, 11.5 s ago")),
            (nameof(TraceText.QueueFull), TraceLevel.Detail, TraceText.QueueFull("G4ABC-1", 30.2)),
            (nameof(TraceText.QueueWritable), TraceLevel.Detail, TraceText.QueueWritable("G4ABC-1")),
            (nameof(TraceText.NoAnswer), TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 1, false)),
            (nameof(TraceText.NoAnswer), TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 2, true)),
            (nameof(TraceText.NoAnswer), TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 2, false)),
            (nameof(TraceText.NoAnswer), TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 3, true)),
            (nameof(TraceText.NoAnswer), TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 3, false)),
            (nameof(TraceText.NoAnswer), TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 4, false)),
            (nameof(TraceText.AnswerWait), TraceLevel.Detail, TraceText.AnswerWait("GB7RDG-1", 6.96)),
            (nameof(TraceText.AnswerInTime), TraceLevel.Detail, TraceText.AnswerInTime("GB7RDG-1")),
            (nameof(TraceText.Found), TraceLevel.Flow, TraceText.Found("GB7RDG-1")),
            (nameof(TraceText.FoundAgain), TraceLevel.Flow, TraceText.FoundAgain("GB7RDG-1")),
            (nameof(TraceText.FoundMoved), TraceLevel.Flow, TraceText.FoundMoved("GB7RDG-1", 9, 5)),
            (nameof(TraceText.LostAfterSilence), TraceLevel.Flow, TraceText.LostAfterSilence("GB7RDG-1")),
            (nameof(TraceText.LostUnheard), TraceLevel.Flow, TraceText.LostUnheard("GB7RDG-1")),
            (nameof(TraceText.LostChangedHands), TraceLevel.Flow, TraceText.LostChangedHands("GB7RDG-1", 17)),
            (nameof(TraceText.LostClash), TraceLevel.Flow, TraceText.LostClash("GB7RDG-1", 17)),
            (nameof(TraceText.ForgotStation), TraceLevel.Detail, TraceText.ForgotStation(9, TraceWords.ForgotUnheard)),
            (nameof(TraceText.ChannelBusyWaiting), TraceLevel.Flow, TraceText.ChannelBusyWaiting(TraceText.Waiting(2, "GB7RDG-1"))),
            (nameof(TraceText.ChannelClearWaiting), TraceLevel.Flow, TraceText.ChannelClearWaiting(TraceText.Waiting(1, "GB7RDG-1"))),
            (nameof(TraceText.ChannelBusy), TraceLevel.Detail, TraceText.ChannelBusy(TraceText.CauseText(AccessCause.HeardBurst))),
            (nameof(TraceText.ChannelBusy), TraceLevel.Detail, TraceText.ChannelBusy(TraceText.CauseText(AccessCause.OwnTransmission))),
            (nameof(TraceText.ChannelClear), TraceLevel.Detail, TraceText.ChannelClear()),
            (nameof(TraceText.CarrierFromRadio), TraceLevel.Detail, TraceText.CarrierFromRadio(true)),
            (nameof(TraceText.AskedToSend), TraceLevel.Detail, TraceText.AskedToSend(TraceWords.SubjectDatagrams, TraceText.WaitOrdinary(0.8))),
            (nameof(TraceText.AskedToSend), TraceLevel.Detail, TraceText.AskedToSend(TraceWords.SubjectReport, TraceText.WaitFirstSlot(0.05))),
            (nameof(TraceText.DrewWaitSlots), TraceLevel.Detail, TraceText.DrewWaitSlots(0.9, 2, 2, 0.9)),
            (nameof(TraceText.CanSendAt), TraceLevel.Detail, TraceText.CanSendAt(18.4)),
            (nameof(TraceText.ClearToSend), TraceLevel.Detail, TraceText.ClearToSend(TraceWords.SubjectDatagrams, 1.2)),
            (nameof(TraceText.HeldBack), TraceLevel.Detail, TraceText.HeldBack(TraceWords.SubjectDatagrams, TraceText.CauseText(AccessCause.HeardBurst))),
            (nameof(TraceText.CallsignSet), TraceLevel.Flow, TraceText.CallsignSet("M0LTE")),
            (nameof(TraceText.TookNumber), TraceLevel.Flow, TraceText.TookNumber(23)),
            (nameof(TraceText.LastIdentificationTold), TraceLevel.Flow, TraceText.LastIdentificationTold(180)),
            (nameof(TraceText.SentIdentification), TraceLevel.Flow, TraceText.SentIdentification("M0LTE", 2.07)),
            (nameof(TraceText.IdentificationDue), TraceLevel.Detail, TraceText.IdentificationDue(TraceWords.DueFirst)),
            (nameof(TraceText.IdentifyingFirst), TraceLevel.Detail, TraceText.IdentifyingFirst(TraceWords.SubjectDatagrams)),
            (nameof(TraceText.NumberChangedInUse), TraceLevel.Flow, TraceText.NumberChangedInUse(23, 41)),
            (nameof(TraceText.NumberChangedTold), TraceLevel.Flow, TraceText.NumberChangedTold(23, 41)),
            (nameof(TraceText.NumberClash), TraceLevel.Flow, TraceText.NumberClash(23)),
            (nameof(TraceText.NumberClashListening), TraceLevel.Flow, TraceText.NumberClashListening(23)),
            (nameof(TraceText.NumberChangedHands), TraceLevel.Flow, TraceText.NumberChangedHands(23)),
            (nameof(TraceText.ToldToMove), TraceLevel.Flow, TraceText.ToldToMove(23)),
            (nameof(TraceText.HeardMoveNotice), TraceLevel.Flow, TraceText.HeardMoveNotice(23)),
            (nameof(TraceText.NoticeCancelled), TraceLevel.Detail, TraceText.NoticeCancelled(23)),
            (nameof(TraceText.BuiltDiscarded), TraceLevel.Detail, TraceText.BuiltDiscarded(TraceWords.DiscardBurst)),
        ];

    private static string[][] Rows() => [.. Doc().Split('\n').Select(line => line.Split('|').Select(c => c.Trim()).ToArray())];

    private static (TraceLevel Level, string Text)[] Documented() =>
        [.. Rows().Where(c => c.Length >= 5 && c[1] is "Flow" or "Detail" && IsCode(c[2])).Select(c => (c[1] == "Flow" ? TraceLevel.Flow : TraceLevel.Detail, c[2][1..^1]))];

    private static bool IsCode(string cell) => cell.Length > 2 && cell.StartsWith('`') && cell.EndsWith('`');

    [Fact]
    public void Every_line_in_the_catalogue_is_what_the_library_says_at_that_level_and_in_that_order()
    {
        Documented().Should().Equal(Said().Select(s => (s.Level, s.Text)));
    }

    [Fact]
    public void Every_method_that_makes_a_line_has_an_example_in_the_catalogue()
    {
        string[] lineMakers =
        [
            .. typeof(TraceText).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(string) && m.GetCustomAttribute<TracePieceAttribute>() is null && !m.IsPrivate)
                .Select(m => m.Name)
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];
        lineMakers.Should().NotBeEmpty();
        string[] exemplified = [.. Said().Select(s => s.Method).Distinct().Order(StringComparer.Ordinal)];
        exemplified.Should().Equal(lineMakers, "a method of TraceText that makes a line needs a row in docs/trace.md and in Said(); one that only makes a piece of a line is marked [TracePiece]");
    }

    [Fact]
    public void Every_fixed_reason_kind_and_cause_is_listed_in_the_document_word_for_word_and_in_order()
    {
        (string Group, string Text)[] listed = [.. Rows().Where(c => c.Length == 4 && c[1].Length > 0 && char.IsUpper(c[1][0]) && c[1] is not "Group" && IsCode(c[2])).Select(c => (c[1], c[2][1..^1]))];
        listed.Should().Equal(TraceWords.All);
    }

    [Fact]
    public void The_report_said_template_is_what_the_station_writes()
    {
        TraceWords.ChoseReportTemplate.Replace("{rate}", TraceText.Rate(70.4), StringComparison.Ordinal).Replace("{age}", TraceText.Seconds(11.5), StringComparison.Ordinal)
            .Should().Be("report said 70.4 kbit/s, 11.5 s ago");
    }

    [Fact]
    public void Every_example_is_plain_ascii_starts_with_a_capital_and_has_no_full_stop_or_exclamation_mark()
    {
        foreach ((TraceLevel _, string text) in Documented())
        {
            text.Should().MatchRegex("^[A-Z][\\x20-\\x7E]*[^.\\x20]$", text);
            text.Should().NotContain("!");
        }
    }

    [Fact]
    public void The_document_has_no_dash_and_the_trace_source_has_no_character_outside_ascii()
    {
        Doc().Should().NotContain("\u2013").And.NotContain("\u2014");
        foreach (string file in new[] { "TraceText.cs", "Station.Trace.cs" })
        {
            string text = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "M0LTE.OfdmFm", file));
            text.Where(c => c > 127).Should().BeEmpty(file);
        }
    }

    private static string Here([CallerFilePath] string here = "") => here;
}
