using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Recognition;

namespace DcsAtc.Tests;

public class ReadbackTests
{
    // The mishearings from data/voice/recognition.json that matter here.
    private static readonly PilotCallReader Reader = new(new Dictionary<string, string> { ["one way"] = "runway", ["white"] = "right", ["adding"] = "heading" });

    /// <summary>What ATC asks to say again, for a readback as the recognizer wrote it (which the reader takes for a readback, not a request).</summary>
    private static string[] Mistakes(string atc, string readback)
    {
        var reading = Reader.Read(readback);
        Assert.Null(reading.Call);
        Assert.True(reading.Readback, readback);
        return [.. Readbacks.Mistakes(Readbacks.Instructed(atc), Readbacks.Heard(reading.Words)).Select(m => m.Said)];
    }

    private const string Taxi = "Hangman 1-2, Al Dhafra Ground, taxi to holding point runway 31R via A, B. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92.";
    private const string Takeoff = "Hangman 1-2, unrestricted climb approved, wind 310 degrees, 10 knots, runway 31R, cleared for takeoff.";
    private const string Vectors = "Hangman 1-2, turn left heading 310, descend and maintain 3000 feet.";
    private const string Vacate = "Hangman 1-2, welcome to Al Dhafra, vacate runway when able, contact Ground on 250.0.";

    [Theory]
    // Readbacks from your bake-off recordings, as Whisper wrote them: missing or misheard words are no mistake.
    [InlineData(Taxi, "Exit to holding point, Runway 31 via Alpha, Bravo, Hangman 1-2.")]
    [InlineData(Taxi, "Taxi to holding point runway 31 right via Alpha, Bravo, QNH 1013, Hangman 1-2.")]
    [InlineData(Takeoff, "Hit for takeoff, one way, 3-1, right, Hangman 1-2.")]
    [InlineData(Vectors, "Turn left heading 3-1-0, descend and maintain 3,000 feet, Hangman 1-2.")]
    [InlineData(Vacate, "Vacating runway, Contact ground 250.0, Hangman 1-2.")]
    [InlineData(Takeoff, "Wilco, Hangman 1-2.")]
    public void A_readback_with_the_right_values_is_fine(string atc, string readback) => Assert.Empty(Mistakes(atc, readback));

    [Fact]
    public void A_different_value_is_said_again_as_ATC_said_it()
    {
        Assert.Equal(["runway 31R"], Mistakes(Taxi, "Taxi to holding point runway 13 left via Alpha, Bravo, Hangman 1-2."));
        Assert.Equal(["runway 31R"], Mistakes(Takeoff, "Cleared for takeoff runway 31 left, Hangman 1-2."));
        Assert.Equal(["QNH 1013"], Mistakes(Taxi, "Taxi to holding point runway 31 right, QNH 1012, Hangman 1-2."));
        Assert.Equal(["heading 310", "3000 feet"], Mistakes(Vectors, "Adding 0, 4, 0, descending 4000, Hangman 1-2."));
        Assert.Equal(["frequency 250.0"], Mistakes(Vacate, "Vacating, contact ground 257.0, Hangman 1-2."));
    }

    [Fact]
    public void The_newest_instruction_with_something_to_compare_is_checked_and_corrected_by_its_station()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0);
        var log = new CallLog(new FakeRadio(), () => now);
        var stations = new AirfieldStations { Ground = 250, Tower = 270 };
        log.Transmit(new RadioCall("Al Dhafra", "Ground", 2, Taxi, stations));
        now = now.AddSeconds(20);
        log.Transmit(new RadioCall("Al Dhafra", "Tower", 2, Takeoff, stations));
        log.Transmit(new RadioCall("Al Dhafra", "Tower", 2, "Hangman 1-2, read you five.", stations));
        log.Transmit(new RadioCall("Al Minhad", "Tower", 2, "Hangman 1-2, runway 27, cleared to land.", stations));
        now = now.AddSeconds(5);

        // The readback of the takeoff clearance (the newest with a runway from Al Dhafra), with the wrong side.
        var correction = log.Correction("Hangman 1-2", "Al Dhafra", Reader.Read("Cleared for takeoff runway 31 left, Hangman 1-2.").Words, TimeSpan.FromMinutes(1));
        Assert.Equal(("Al Dhafra Tower", "Hangman 1-2, negative, I say again, runway 31R."), (correction?.Station, correction?.Text));
        // QNH: only the taxi clearance had one.
        Assert.Equal("Hangman 1-2, negative, I say again, QNH 1013.",
            log.Correction("Hangman 1-2", "Al Dhafra", Reader.Read("QNH 1012, Hangman 1-2.").Words, TimeSpan.FromMinutes(1))?.Text);
        // Right, nothing to compare, or too old: no answer.
        Assert.Null(log.Correction("Hangman 1-2", "Al Dhafra", Reader.Read("Cleared for takeoff runway 31 right, Hangman 1-2.").Words, TimeSpan.FromMinutes(1)));
        Assert.Null(log.Correction("Hangman 1-2", "Al Dhafra", Reader.Read("Wilco, Hangman 1-2.").Words, TimeSpan.FromMinutes(1)));
        now = now.AddMinutes(2);
        Assert.Null(log.Correction("Hangman 1-2", "Al Dhafra", Reader.Read("Cleared for takeoff runway 31 left, Hangman 1-2.").Words, TimeSpan.FromMinutes(1)));
    }
}
