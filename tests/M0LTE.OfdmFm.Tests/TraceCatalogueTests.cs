using System.Runtime.CompilerServices;
using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>The catalogue in docs/trace.md and the words the library says are the same: each example in a table is what <see cref="TraceText"/> makes for it, at the level the table gives, and no shape is missing from the document.</summary>
public class TraceCatalogueTests
{
    private static string Doc([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "trace.md"));

    /// <summary>What the library says for the example of each row of the catalogue, with the level the row gives, in the order of the document.</summary>
    private static (TraceLevel Level, string Text)[] Said() =>
        [
            (TraceLevel.Flow, TraceText.HeardForUs("GB7RDG-1", 2, 31.2)),
            (TraceLevel.Flow, TraceText.HeardForEveryone("GB7RDG-1", 1, 28)),
            (TraceLevel.Flow, TraceText.HeardForEveryoneAsking("GB7RDG-1", 1, "G4ABC-1", 28)),
            (TraceLevel.Flow, TraceText.HeardForOther("GB7RDG-1", 3, "station 17", 40)),
            (TraceLevel.Flow, TraceText.HeardForUs("GB7RDG-1", 0, 31.2)),
            (TraceLevel.Flow, TraceText.HeardNotDecoded("station 9")),
            (TraceLevel.Flow, TraceText.HeardUnreadable("station 9")),
            (TraceLevel.Flow, TraceText.HeardIdentified("GB7RDG-1", "GB7RDG", 33)),
            (TraceLevel.Flow, TraceText.LandedOn("GB7RDG-1")),
            (TraceLevel.Flow, TraceText.DroppedBurst("GB7RDG-1")),
            (TraceLevel.Flow, TraceText.LostBurst("GB7RDG-1")),
            (TraceLevel.Detail, TraceText.HeaderNotUsed("a version this station does not know")),
            (TraceLevel.Flow, TraceText.PathGood("GB7RDG-1", 70.4, 54.9)),
            (TraceLevel.Detail, TraceText.Measured("GB7RDG-1", 31.2, 29.84, 3.0, 70.4, 31, 5, false, null, null)),
            (TraceLevel.Flow, TraceText.Told("GB7RDG-1", 70.4, false)),
            (TraceLevel.Flow, TraceText.Told("GB7RDG-1", 12.5, true)),
            (TraceLevel.Flow, TraceText.ToldHere("GB7RDG-1", "G4ABC-1")),
            (TraceLevel.Flow, TraceText.ToldBy("GB7RDG-1", 70.4, false)),
            (TraceLevel.Flow, TraceText.ToldBy("GB7RDG-1", 12.5, true)),
            (TraceLevel.Detail, TraceText.OwingReport("GB7RDG-1", "its burst was not decoded")),
            (TraceLevel.Detail, TraceText.ReportCovered("GB7RDG-1")),
            (TraceLevel.Detail, TraceText.AnnouncementCovered("G4ABC-1", "GB7RDG-1")),
            (TraceLevel.Detail, TraceText.HostToldNotDecoded("G4ABC-1", 2)),
            (TraceLevel.Detail, TraceText.HoldingOrdinary("GB7RDG-1", 12.26)),
            (TraceLevel.Detail, TraceText.GoingStraightOn("GB7RDG-1")),
            (TraceLevel.Flow, TraceText.Sent(3, "GB7RDG-1", 70.4, 0.35, true)),
            (TraceLevel.Flow, TraceText.Sent(1, TraceText.EveryoneFor("G4ABC-1"), 0.25, 1.4, false)),
            (TraceLevel.Flow, TraceText.Sent(2, "everyone", 0.25, 2.07, false)),
            (TraceLevel.Flow, TraceText.RefusedTooLarge("G4ABC-1", 400, 333, 0.25)),
            (TraceLevel.Flow, TraceText.RefusedFull("G4ABC-1", 30.4)),
            (TraceLevel.Flow, TraceText.RefusedNoCallsign("G4ABC-1")),
            (TraceLevel.Flow, TraceText.DroppedCancelled("G4ABC-1", 2)),
            (TraceLevel.Flow, TraceText.DroppedNoLongerFits("G4ABC-1", 0.25)),
            (TraceLevel.Detail, TraceText.Queued(30, "G4ABC-1", 2)),
            (TraceLevel.Detail, TraceText.Chose(54.9, "G4ABC-1", 29, "report said 70.4 kbit/s, 11.5 s ago")),
            (TraceLevel.Detail, TraceText.QueueFull("G4ABC-1", 30.2)),
            (TraceLevel.Detail, TraceText.QueueWritable("G4ABC-1")),
            (TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 1)),
            (TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 2)),
            (TraceLevel.Flow, TraceText.NoAnswer("GB7RDG-1", 3)),
            (TraceLevel.Detail, TraceText.AnswerWait("GB7RDG-1", 6.96)),
            (TraceLevel.Detail, TraceText.AnswerInTime("GB7RDG-1")),
            (TraceLevel.Flow, TraceText.Found("GB7RDG-1")),
            (TraceLevel.Flow, TraceText.FoundAgain("GB7RDG-1")),
            (TraceLevel.Flow, TraceText.FoundMoved("GB7RDG-1", 9, 5)),
            (TraceLevel.Flow, TraceText.LostAfterSilence("GB7RDG-1")),
            (TraceLevel.Flow, TraceText.LostUnheard("GB7RDG-1")),
            (TraceLevel.Flow, TraceText.LostChangedHands("GB7RDG-1", 17)),
            (TraceLevel.Detail, TraceText.ForgotStation(9, "not heard for 10 minutes")),
            (TraceLevel.Flow, TraceText.ChannelBusyWaiting(TraceText.Waiting(2, "GB7RDG-1"))),
            (TraceLevel.Flow, TraceText.ChannelClearWaiting(TraceText.Waiting(1, "GB7RDG-1"))),
            (TraceLevel.Detail, TraceText.ChannelBusy(TraceText.CauseText(AccessCause.HeardBurst))),
            (TraceLevel.Detail, TraceText.ChannelClear()),
            (TraceLevel.Detail, TraceText.CarrierFromRadio(true)),
            (TraceLevel.Detail, TraceText.AskedToSend("datagrams", TraceText.WaitOrdinary(0.8))),
            (TraceLevel.Detail, TraceText.AskedToSend("a report", TraceText.WaitFirstSlot(0.05))),
            (TraceLevel.Detail, TraceText.DrewWaitSlots(0.9, 2, 2, 0.9)),
            (TraceLevel.Detail, TraceText.CanSendAt(18.4)),
            (TraceLevel.Detail, TraceText.ClearToSend("datagrams", 1.2)),
            (TraceLevel.Detail, TraceText.HeldBack("datagrams", "another station is sending")),
            (TraceLevel.Flow, TraceText.CallsignSet("M0LTE")),
            (TraceLevel.Flow, TraceText.TookNumber(23)),
            (TraceLevel.Flow, TraceText.SentIdentification("M0LTE", 2.07)),
            (TraceLevel.Detail, TraceText.IdentificationDue(true)),
            (TraceLevel.Flow, TraceText.NumberChangedInUse(23, 41)),
            (TraceLevel.Flow, TraceText.NumberChangedTold(23, 41)),
            (TraceLevel.Flow, TraceText.NumberClash(23)),
            (TraceLevel.Flow, TraceText.NumberClashListening(23)),
            (TraceLevel.Flow, TraceText.NumberChangedHands(23)),
            (TraceLevel.Flow, TraceText.ToldToMove(23)),
            (TraceLevel.Flow, TraceText.HeardMoveNotice(23)),
            (TraceLevel.Detail, TraceText.NoticeCancelled(23)),
            (TraceLevel.Detail, TraceText.BuiltDiscarded("burst")),
        ];

    private static (TraceLevel Level, string Text)[] Documented()
    {
        var found = new List<(TraceLevel, string)>();
        foreach (string line in Doc().Split('\n'))
        {
            string[] cells = line.Split('|');
            if (cells.Length >= 4 && cells[1].Trim() is "Flow" or "Detail" && cells[2].Trim().StartsWith('`') && cells[2].Trim().EndsWith('`'))
            {
                string cell = cells[2].Trim();
                found.Add((cells[1].Trim() == "Flow" ? TraceLevel.Flow : TraceLevel.Detail, cell[1..^1]));
            }
        }

        return [.. found];
    }

    [Fact]
    public void Every_line_in_the_catalogue_is_what_the_library_says_at_that_level_and_in_that_order()
    {
        Documented().Should().Equal(Said());
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
