using DcsAtc.Recognition;

namespace DcsAtc.Tests;

public class RecognitionTests
{
    private static string Plain(string text) => Transcript.Plain(text);

    [Theory]
    [InlineData("runway 31R", "runway thirty-one right", "runway three one right", "Runway 3-1 right")]
    [InlineData("QNH 1013", "Q N H one zero one three", "QNH ten thirteen", "q.n.h. 1013")]
    [InlineData("altimeter 29.92", "altimeter two niner niner two", "Altimeter 2992", "Altimeter 29A9A2")]
    [InlineData("climb and maintain 3000 feet", "climb and maintain three thousand feet", "Climb and maintain 3,000 feet", "climb and maintain 3-0-0-0 feet")]
    [InlineData("maintain 3500 feet", "maintain three thousand five hundred feet", "Maintain 3,500 feet", "maintain three five zero zero feet")]
    [InlineData("contact Ground 250.0", "contact ground two five zero decimal zero", "Contact Ground 250 point 0", "contact ground two five zero zero")]
    [InlineData("via A, B", "via Alpha, Bravo", "Via alpha bravo", "via Alfa, Bravo.")]
    [InlineData("via D2, H1", "via Delta 2, Hotel 1", "via delta two hotel one", "Via D 2, H 1.")]
    [InlineData("Hangman 1-2", "Hangman one two", "Hangman 12", "hangman, 1, 2")]
    [InlineData("Al Dhafra Ground", "Aldafra Ground", "Al-Dhafra ground", "AL Dafra Ground")]
    [InlineData("angels 15", "angels fifteen", "Angels 1-5", "angels one five")]
    [InlineData("request startup", "request start-up", "Request start up", "request startup.")]
    [InlineData("cleared for takeoff", "cleared for take-off", "Cleared for take off", "cleared for takeoff")]
    [InlineData("descend 4000", "descent 4000", "Descend four thousand", "descend 4,000")]
    [InlineData("40 miles northwest", "forty miles north west", "40 miles north-west", "four zero miles northwest")]
    public void Different_ways_of_writing_a_call_compare_equal(string a, string b, string c, string d)
    {
        Assert.Equal(Plain(a), Plain(b));
        Assert.Equal(Plain(a), Plain(c));
        Assert.Equal(Plain(a), Plain(d));
    }

    [Fact]
    public void Plain_form_reads_like_the_radio()
    {
        Assert.Equal("al dhafra ground hangman one two request taxi", Plain("Al Dhafra Ground, Hangman 1-2, request taxi."));
        Assert.Equal("heading zero four zero descend four thousand", Plain("Heading 040, descend 4000."));
        Assert.Equal("i say again go around", Plain("I say again, go around."));
        Assert.Equal("hold over the tacan at seven thousand", Plain("Hold over the TACAN at 7000"));
        Assert.Equal("ils runway one three left", Plain("I-L-S runway 13L"));
        Assert.Equal("taxi to apron one west", Plain("Taxi to Apron 1 West"));
    }

    [Fact]
    public void Word_errors_count_substitutions_insertions_and_deletions()
    {
        string[] reference = Transcript.Normalize("cleared for takeoff runway 31R");
        var (errors, correct) = Transcript.Compare(reference, reference);
        Assert.Equal(0, errors);
        Assert.All(correct, Assert.True);

        (errors, correct) = Transcript.Compare(reference, Transcript.Normalize("cleared for take off runway three one bite"));
        Assert.Equal(1, errors);
        Assert.Equal([true, true, true, true, true, true, true, false], correct);

        (errors, _) = Transcript.Compare(reference, Transcript.Normalize("uh cleared takeoff runway three one right please"));
        Assert.Equal(2, errors); // "for" missing, "please" added; the filler does not count
    }

    [Fact]
    public void Key_phrases_must_appear_in_order()
    {
        var heard = Transcript.Normalize("Al Dhafra Tower, Hangman 1-2, holding short runway 31 right, ready for departure");
        Assert.True(Transcript.Contains(heard, Transcript.Normalize("runway 31R")));
        Assert.True(Transcript.Contains(heard, Transcript.Normalize("ready for departure")));
        Assert.False(Transcript.Contains(heard, Transcript.Normalize("runway 13L")));
        Assert.False(Transcript.Contains(heard, Transcript.Normalize("departure ready")));
    }

    [Fact]
    public void Test_script_marks_key_phrases_and_skips_comments()
    {
        var calls = TestCall.Parse(["# comment", "", "Al Dhafra Ground, Hangman 1-2, [request taxi].", "  Taxi to [holding point] [runway 31 right] via [Alpha], Hangman 1-2.  "]);
        Assert.Equal(2, calls.Count);
        Assert.Equal(1, calls[0].Number);
        Assert.Equal("Al Dhafra Ground, Hangman 1-2, request taxi.", calls[0].Text);
        Assert.Equal(["request taxi"], calls[0].Keys);
        Assert.Equal(2, calls[1].Number);
        Assert.Equal("Taxi to holding point runway 31 right via Alpha, Hangman 1-2.", calls[1].Text);
        Assert.Equal(["holding point", "runway 31 right", "Alpha"], calls[1].Keys);
    }

    [Fact]
    public void The_test_calls_all_have_key_phrases_that_survive_normalizing()
    {
        var calls = TestCall.Load(Path.Combine(TestPaths.RepoRoot, "data", "voice", "test-calls.txt"));
        Assert.InRange(calls.Count, 45, 60);
        foreach (var call in calls)
        {
            Assert.NotEmpty(call.Keys);
            var words = Transcript.Normalize(call.Text);
            // What was said, written out, contains every key phrase: the script itself scores 100 %.
            Assert.All(call.Keys, k => Assert.True(Transcript.Contains(words, Transcript.Normalize(k)), $"call {call.Number}: \"{k}\""));
        }
    }

    [Fact]
    public void Recognizer_catalog_is_consistent()
    {
        var catalog = RecognizerCatalog.Load(Path.Combine(TestPaths.RepoRoot, "data", "voice"));
        Assert.NotNull(catalog.Find(catalog.Default));
        Assert.All(catalog.Recognizers, r =>
        {
            Assert.NotNull(catalog.ModelFor(r));
            Assert.Contains(r.Engine, new[] { "whisper", "parakeet" });
            Assert.Contains(r.Runtime, new[] { "cpu", "vulkan" });
        });
        Assert.Equal(catalog.Recognizers.Count, catalog.Recognizers.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Al Dhafra", catalog.Vocabulary.Prompt());
        Assert.Contains("Hangman", catalog.Vocabulary.Hotwords());
    }

    [Fact]
    public void Local_files_add_their_own_recognizers_and_models()
    {
        string dir = Path.Combine(Path.GetTempPath(), "digitower-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, RecognizerCatalog.File), """
                { "default": "whisper-turbo-vulkan", "threads": 4,
                  "recognizers": [ { "id": "whisper-turbo-vulkan", "engine": "whisper", "model": "ggml-turbo", "runtime": "vulkan" } ],
                  "models": [ { "name": "ggml-turbo", "url": "u", "license": "MIT", "sizeMb": 574 } ] }
                """);
            File.WriteAllText(Path.Combine(dir, RecognizerCatalog.LocalFile), """{ "threads": 2 }""");
            // The user's own file (beside the settings), as tools/whisper-train/register.py writes it.
            string user = Path.Combine(dir, "user.json");
            File.WriteAllText(user, """
                { "models": [ { "name": "ggml-turbo-tuned-q5_0", "license": "your own fine-tune", "sizeMb": 547 } ],
                  "recognizers": [ { "id": "whisper-turbo-tuned-vulkan", "label": "Whisper turbo tuned on my voice, prompt (GPU, Vulkan)",
                                     "engine": "whisper", "model": "ggml-turbo-tuned-q5_0", "runtime": "vulkan" } ] }
                """);

            var catalog = RecognizerCatalog.Load(dir, userFile: user);
            Assert.Equal(("whisper-turbo-vulkan", 2), (catalog.Default, catalog.Threads));
            Assert.Equal(["whisper-turbo-vulkan", "whisper-turbo-tuned-vulkan"], catalog.Recognizers.Select(r => r.Id));
            var tuned = catalog.Find("whisper-turbo-tuned-vulkan")!;
            Assert.Equal(("ggml-turbo-tuned-q5_0", null), (catalog.ModelFor(tuned)!.Name, catalog.ModelFor(tuned)!.Url));
            Assert.False(catalog.IsAvailable(tuned));   // not converted yet
            File.WriteAllText(Path.Combine(dir, "models", "ggml-turbo-tuned-q5_0.bin").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), "x");
            Assert.True(catalog.IsAvailable(tuned));

            // Without the user's file: only the shared ones.
            Assert.Single(RecognizerCatalog.Load(dir).Recognizers);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>Loads a speech model (CPU-heavy): kept apart from the tests that measure real-time SRS timing.</summary>
[Collection(TimingCollection.Name)]
public class RecognizerModelTests
{
    [SkippableFact]
    public void Parakeet_understands_a_synthetic_call()
    {
        var catalog = RecognizerCatalog.Load(Path.Combine(TestPaths.RepoRoot, "data", "voice"));
        var info = catalog.Find("parakeet")!;
        Skip.IfNot(catalog.IsAvailable(info), "Parakeet model not downloaded (VoiceTool fetch --recognizer parakeet)");
        var voices = DcsAtc.Voice.VoiceCatalog.Load(Path.Combine(TestPaths.RepoRoot, "data", "voice"));
        var voice = voices.Find(voices.Default)!;
        Skip.IfNot(voices.IsAvailable(voice), "TTS voice not downloaded");

        using var tts = voices.Open(voice);
        var clip = DcsAtc.Voice.Audio.Resample(tts.Render("Al Dhafra Ground, Hangman 1-2, request taxi."), 16000);
        using var recognizer = catalog.Create(info, threads: 2);
        var heard = Transcript.Normalize(recognizer.Recognize(clip.Samples));
        Assert.True(Transcript.Contains(heard, ["request", "taxi"]), string.Join(' ', heard));
    }
}

public class PilotCallTests
{
    // The aliases from data/voice/recognition.json: DiGii's consistent mishearings.
    private static readonly PilotCallReader Reader = new(RecognizerCatalog.Load(Path.Combine(TestPaths.RepoRoot, "data", "voice")).Aliases);

    private static string Read(string text, string station = "Al Dhafra Ground")
    {
        var r = Reader.Read(text, station);
        return r.Call is { } c ? $"{c.Intent}{(c.Option is { } o ? $" {o}" : "")}" : r.Readback ? "readback" : r.AimedAtAtc ? "say again" : "chatter";
    }

    // What the recognizer wrote for DiGii's calls (the S5 recordings and a test flight).
    [Theory]
    [InlineData("Dhafra Ground, Hangman 1-2, request taxi.", "RequestTaxi")]
    [InlineData("Bei Dhafra Ground, Hangman 1-2, request taxi.", "RequestTaxi")]
    [InlineData("Dhafra Ground, Hangman 1-2, say again.", "SayAgain")]
    [InlineData("Ground, Hangman 1-2, stay again.", "SayAgain")]
    [InlineData("Al Dhafra Hangman 1-2, request TACAN approach.", "RequestApproach tacan")]
    [InlineData("Adhafra, you are the greatest. I love you. Hangman 1-2, request ILS at first.", "RequestApproach ils")]
    [InlineData("Power, Hangman 1-2, ready for departure on navigation.", "ReadyForDeparture own")]
    [InlineData("Al Dhafra Tower, Hangman 1-2, holding short runway 31 right, ready for departure.", "ReadyForDeparture")]
    [InlineData("Tower, Hangman 1-2, ready for departure, request closed traffic.", "ReadyForDeparture closed")]
    [InlineData("Approach, Hangman 1-2, healed inside.", "FieldInSight")]
    [InlineData("Bidafra Approach, Hangman 1-2, 40 miles northwest, Angelus 15, request ILS Approach, Runway 31 right.", "RequestApproach ils")]
    [InlineData("Al Dhafra Ground, Hangman 1-2, vacated runway 31 right, request taxi to parking.", "TaxiToParking")]
    [InlineData("Approach, Hangman 1-2, cancel approach.", "CancelApproach")]
    [InlineData("Adafra Tower, Hangman 1-2, checking in, 8 miles, ILS 1-way 31 right.", "CheckIn")]
    [InlineData("Al Dhafra Approach, Hangman 1-2, airborne, passing 2000 for angels 10.", "CheckIn")]
    [InlineData("Power, Hangman 1-2, 5-mal, final, gear down.", "ReportFinal")]
    [InlineData("Al Dhafra Ground, Hangman 1-2, radio check.", "RadioCheck")]
    [InlineData("Al Dhafra Ground, Hangman 1-2, request start-up.", "RequestStartup")]
    [InlineData("Al Dhafra Ground, Hangman 1-2, request startup.", "RequestStartup")]
    [InlineData("Al Dhafra Ground, Hangman 1 flight, ready to start.", "RequestStartup")]
    // A test flight: "request takeoff" was read as "request taxi" (two letters apart) before it was a phrase of its own.
    [InlineData("Dhafra Tower, Hangman 1-2, request takeoff, departure north.", "ReadyForDeparture north")]
    [InlineData("Adafra Tower, Hangman 1-2, request take off.", "ReadyForDeparture")]
    [InlineData("Adafra Tower, Hangman 1-2, ready for take-off.", "ReadyForDeparture")]
    [InlineData("Alhambra Ground, Hangman 1-2, request taxi.", "RequestTaxi")]
    [InlineData("Al Dhafra Apo, Hangman 1-2, check in.", "CheckIn")]
    [InlineData("Adafra Approach, Hangman 1-2, inbound.", "RequestLanding")]
    // Emergencies come before anything else in the call; the cancellation before the emergency.
    [InlineData("Mayday, Mayday, Mayday, Al Dhafra Tower, Hangman 1-2, engine fire, request landing.", "Mayday")]
    [InlineData("Pan-pan, pan-pan, pan-pan, Approach, Hangman 1-2, hydraulic failure.", "PanPan")]
    [InlineData("Approach, Hangman 1-2, cancel Mayday.", "CancelEmergency")]
    // The model tuned on DiGii's voice: "gear down" heard as "cleared down" made the call a readback.
    [InlineData("Tower, Hangman 1-2, 5 miles final, cleared down.", "ReportFinal")]
    [InlineData("Approach, Hangman 1-2, request Tekken approach.", "RequestApproach tacan")]
    [InlineData("Approach, Hangman 1-2, request TAECAN approach.", "RequestApproach tacan")]
    public void Reads_the_request(string heard, string expected) => Assert.Equal(expected, Read(heard));

    // Readbacks never trigger a request, even when they contain one's words ("ILS approach", "for takeoff").
    [Theory]
    [InlineData("Hit for takeoff, one way, 3-1, right, Hangman 1-2.")]
    [InlineData("Did IOS approach runway 31 white, Hangman 1-2?")]
    [InlineData("Cleared ILS approach runway 31 right, Hangman 1-2.")]
    [InlineData("Exit to holding point, Runway 31 via Alpha, Bravo, Hangman 1-2.")]
    [InlineData("Watcher, Hangman 1-2.")]
    [InlineData("Atimeter, 2, Niner, Decimal, Niner, 2, Hangman 1-2.")]
    [InlineData("Tower, Hangman 1-2, full stop.")]
    [InlineData("Runway 11, backtrack and line up, Hangman 1-2.")]
    [InlineData("Backtrack and vacate via Delta, Hangman 1-2.")]
    public void Readbacks_get_no_answer(string heard) => Assert.Contains(Read(heard), new[] { "readback", "chatter" });

    // Near-misses of a few letters are accepted, but not a different word in a short phrase.
    [Theory]
    [InlineData("Tower, Hangman 1-2, request take a look.")]
    [InlineData("Ground, Hangman 1-2, request takeout.")]
    public void A_different_word_is_not_a_near_miss(string heard) => Assert.NotEqual("RequestTaxi", Read(heard));

    [Fact]
    public void A_call_to_ATC_that_is_not_understood_gets_say_again_chatter_gets_nothing()
    {
        Assert.Equal("say again", Read("Al Dhafra Tower, Hangman 1-2, blah blah blah.", "Al Dhafra Tower"));
        Assert.Equal("say again", Read("Tower, Hangman 1-2, request something odd.", "Al Dhafra Tower"));
        Assert.Equal("chatter", Read("Nice landing mate, see you at the bar.", "Al Dhafra Tower"));
    }

    [Fact]
    public void Without_emergencies_the_rest_of_the_call_counts()
    {
        var reader = new PilotCallReader(emergencies: false);
        Assert.Equal(PilotIntent.RequestLanding, reader.Read("Mayday, Mayday, Mayday, Al Dhafra Tower, Hangman 1-2, request landing.").Call?.Intent);
    }

    [Fact]
    public void Aliases_replace_whole_words_only()
    {
        var reader = new PilotCallReader(new Dictionary<string, string> { ["one way"] = "runway" });
        Assert.Equal(["cleared", "to", "land", "runway", "three", "one", "right"], reader.Read("cleared to land one way 31R").Words);
        Assert.Equal(["someway"], reader.Read("someway").Words);
    }

    [Fact]
    public void Taxiway_letters_the_tuned_model_misspells_are_read_right()
    {
        var words = Reader.Read("Vaya Papa, Ubeck, Hotel, Hotel 1, runway 31 right, Hangman 1-2.").Words;
        Assert.Contains("quebec", words);
        Assert.Contains("foxtrot", Reader.Read("Taxi to holding point runway 13 left via Alpha, Foxtrott, QNH 1013, Hangman 1-2.").Words);
    }
}
