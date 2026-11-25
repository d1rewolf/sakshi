namespace Sakshi.Transcribe.Tests;

public class TranscribeCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _model;

    public TranscribeCommandTests()
    {
        Directory.CreateDirectory(_dir);
        _model = Path.Combine(_dir, "ggml-fake.bin");
        File.WriteAllText(_model, "not a model");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class FakeWhisper(params Segment[] segments) : IWhisper
    {
        public string? Language, Model;
        public int MaxContext = int.MinValue;
        public bool Called;
        public string? Wav;
        public bool? Words;
        public Exception? Throw;

        public Task<IReadOnlyList<Segment>> TranscribeAsync(
            string wavPath, string modelPath, string language, int maxContext, CancellationToken ct, bool words = false)
        {
            Called = true;
            Wav = wavPath;
            Words = words;
            if (Throw is not null) throw Throw;
            Language = language; Model = modelPath; MaxContext = maxContext;
            Assert.True(File.Exists(wavPath));
            return Task.FromResult<IReadOnlyList<Segment>>(segments);
        }
    }

    [Fact]
    public async Task Log_option_sends_both_streams_to_the_file_and_overwrites_it()
    {
        var log = Path.Combine(_dir, "t.log");
        File.WriteAllText(log, "stale");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await TranscribeCommand.RunAsync(
            ["--log", log, Path.Combine(_dir, "missing.mp4")], new FakeWhisper(), stdout, stderr, _dir,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Equal("", stdout.ToString() + stderr.ToString());
        Assert.Contains("Error: input file not found", File.ReadAllText(log));
        Assert.DoesNotContain("stale", File.ReadAllText(log));
    }

    [Fact]
    public async Task Log_option_UsageError_GoesToTheLogAsWellAsStderr()
    {
        var log = Path.Combine(_dir, "t.log");
        File.WriteAllText(log, "stale");
        var stderr = new StringWriter();
        var code = await TranscribeCommand.RunAsync(
            ["--log", log, "-F", "bogus", "x.mp4"], new FakeWhisper(), new StringWriter(), stderr, _dir,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, code);
        Assert.StartsWith("Error: invalid --frames mode: bogus", stderr.ToString());
        var text = File.ReadAllText(log);
        Assert.StartsWith("Error: invalid --frames mode: bogus", text);
        Assert.Contains("Usage: ", text);
    }

    [Fact]
    public async Task Rerun_RemovesStaleDerivedFrames_KeepsEverythingElse()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var outDir = Path.Combine(_dir, "out");
        var framesDir = Path.Combine(outDir, "frames");
        Directory.CreateDirectory(framesDir);
        // Left by an interrupted run: raw_0000 sorts first and would shift every timestamp.
        foreach (var stale in new[] { "raw_0000.jpg", "frame_00-59-59.jpg", ".hashlist.txt" })
            File.WriteAllText(Path.Combine(framesDir, stale), "stale");
        File.WriteAllText(Path.Combine(framesDir, ".metadata.txt"), "frame:0 pts:0 pts_time:3599\n");
        File.WriteAllText(Path.Combine(framesDir, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(outDir, "frame_00-00-01.jpg"), "not in frames/");

        var (code, _, _) = await RunAsync(new FakeWhisper(new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(1), " hi")),
            "-F", "changes", "-m", _model, "-o", outDir, clip);

        Assert.Equal(0, code);
        Assert.False(File.Exists(Path.Combine(framesDir, "frame_00-59-59.jpg")));
        Assert.False(File.Exists(Path.Combine(framesDir, ".hashlist.txt")));
        Assert.DoesNotContain(Directory.GetFiles(framesDir, "*.jpg"), f => File.ReadAllText(f) == "stale");
        Assert.NotEmpty(Directory.GetFiles(framesDir, "frame_*.jpg"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(framesDir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(outDir, "frame_00-00-01.jpg")));
        Assert.DoesNotContain("00:59:59", File.ReadAllText(Path.Combine(outDir, "meeting.md")));
    }

    [Fact]
    public async Task Keeping_only_the_sound_leaves_an_m4a_with_the_audio_and_no_picture()
    {
        var clip = await MakeClipAsync();
        var m4a = await Ffmpeg.KeepAudioOnlyAsync(clip, TestContext.Current.CancellationToken);

        Assert.Equal(Path.ChangeExtension(clip, ".m4a"), m4a);
        Assert.False(File.Exists(clip));
        var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffprobe")
        {
            ArgumentList = { "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", m4a },
            RedirectStandardOutput = true,
        })!;
        var streams = (await probe.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        await probe.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["audio"], streams);
    }

    private async Task<string> MakeClipAsync()
    {
        var clip = Path.Combine(_dir, "clip.mp4");
        var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            ArgumentList =
            {
                // A still picture: "changes" mode keeps a frame once it has held still for a second.
                "-y", "-loglevel", "error", "-f", "lavfi", "-i", "smptebars=duration=3:size=320x240:rate=10",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-shortest", clip,
            },
            RedirectStandardError = true,
        })!;
        await gen.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, gen.ExitCode);
        return clip;
    }

    private sealed class FakeSpeakers(Func<string, IReadOnlyList<Segment>, SpeakerOutcome> result) : ISpeakerStep
    {
        public int Calls;
        public string? Source;
        public bool? MeetingMdExisted;
        public bool? FramesWasNull;

        public Task<SpeakerOutcome> LabelAsync(string audioWav, string outputDir, IReadOnlyList<Segment> segments,
            string source, IReadOnlyList<FrameRecord>? frames, TextWriter stdout, CancellationToken ct)
        {
            Calls++;
            Source = source;
            FramesWasNull = frames is null;
            MeetingMdExisted = File.Exists(Path.Combine(outputDir, "meeting.md"));
            return Task.FromResult(result(outputDir, segments));
        }
    }

    private static FakeSpeakers TwoSpeakers() => new((_, segs) => new SpeakerOutcome(
        [segs[0] with { Speaker = "S1", SpeakerLabel = "Ada" }, segs[1] with { Speaker = "S2", SpeakerLabel = "Speaker 2" }],
        2, 0, 1));

    private async Task<(int code, string stdout, string stderr)> RunWithAsync(
        IWhisper w, ISpeakerStep? step, bool configSpeakers, string? cwd, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await TranscribeCommand.RunAsync(args, w, o, e, Path.Combine(_dir, "models"),
            TestContext.Current.CancellationToken, currentDirectory: cwd, speakers: step, configSpeakers: configSpeakers);
        return (code, o.ToString(), e.ToString());
    }

    private static FakeWhisper TwoSegments() => new(
        new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(2), " hello there"),
        new Segment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), " bye"));

    [Fact]
    public async Task SpeakersOffLeavesOutputUnchanged()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var w = TwoSegments();
        var step = TwoSpeakers();
        var outDir = Path.Combine(_dir, "off");

        var (code, stdout, stderr) = await RunWithAsync(w, step, true, null, "--no-speakers", "-F", "none", "-m", _model, "-o", outDir, clip);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal(0, step.Calls);
        Assert.False(w.Words);
        Assert.DoesNotContain("Finding speakers", stdout);
        Assert.DoesNotContain("speaker", File.ReadAllText(Path.Combine(outDir, "transcript.json")));
        Assert.DoesNotContain("- Speakers:", File.ReadAllText(Path.Combine(outDir, "meeting.md")));
    }

    [Fact]
    public async Task NoStepMeansNoLabelsWhateverTheConfigSays()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var w = TwoSegments();

        var (code, _, _) = await RunWithAsync(w, null, true, null, "-F", "none", "-m", _model, "-o", Path.Combine(_dir, "nostep"), clip);

        Assert.Equal(0, code);
        Assert.False(w.Words);
    }

    [Fact]
    public async Task SpeakersOnLabelsTheTranscript()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var w = TwoSegments();
        var step = TwoSpeakers();
        var outDir = Path.Combine(_dir, "on");

        var (code, stdout, stderr) = await RunWithAsync(w, step, true, null, "-F", "none", "-m", _model, "-o", outDir, clip);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.True(w.Words);
        Assert.Equal(1, step.Calls);
        Assert.Equal("clip.mp4", step.Source);
        Assert.True(step.FramesWasNull);
        Assert.Contains("==> Finding speakers", stdout);
        Assert.Contains("   found 2 speakers (1 to name)", stdout);
        var json = File.ReadAllText(Path.Combine(outDir, "transcript.json"));
        Assert.Contains("\"speaker\": \"S1\"", json);
        Assert.Contains("\"speaker_label\": \"Ada\"", json);
        var md = File.ReadAllText(Path.Combine(outDir, "meeting.md"));
        Assert.Contains("- Speakers: ", md);
        Assert.Contains("Ada", File.ReadAllText(Path.Combine(outDir, "transcript.srt")));
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));
    }

    [Fact]
    public async Task TheUnlabelledMeetingExistsWhenTheStepRuns()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var step = TwoSpeakers();

        await RunWithAsync(TwoSegments(), step, true, null, "-F", "none", "-m", _model, "-o", Path.Combine(_dir, "order"), clip);

        Assert.True(step.MeetingMdExisted);
    }

    [Fact]
    public async Task AFailureWhileWritingTheLabelsLeavesAllThreeFilesUnlabelled()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        // The step makes meeting.md's temp name a folder, so that write fails after the others' temps are written.
        var step = new FakeSpeakers((dir, segs) =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "meeting.md.tmp"));
            Directory.CreateDirectory(Path.Combine(dir, "speakers"));
            File.WriteAllText(Path.Combine(dir, "speakers", "S1.wav"), "wav");
            File.WriteAllText(Path.Combine(dir, "speakers.json"), "{}");
            return new SpeakerOutcome(
                [segs[0] with { Speaker = "S1", SpeakerLabel = "Ada" }, segs[1]], 1, 0, 1);
        });
        var outDir = Path.Combine(_dir, "atomic");

        var (code, _, stderr) = await RunWithAsync(TwoSegments(), step, true, null, "-F", "none", "-m", _model, "-o", outDir, clip);

        Assert.Equal(0, code);
        Assert.Contains("Warning: speaker labels skipped:", stderr);
        Assert.False(File.Exists(Path.Combine(outDir, "speakers.json")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "speakers")));
        Assert.DoesNotContain("speaker", File.ReadAllText(Path.Combine(outDir, "transcript.json")));
        Assert.DoesNotContain("Ada", File.ReadAllText(Path.Combine(outDir, "transcript.srt")));
        Assert.DoesNotContain("- Speakers:", File.ReadAllText(Path.Combine(outDir, "meeting.md")));
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));
    }

    [Fact]
    public async Task OneSpeakerIsSingular()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var step = new FakeSpeakers((_, segs) => new SpeakerOutcome(
            [segs[0] with { Speaker = "S1", SpeakerLabel = "Ada" }, segs[1] with { Speaker = "S1", SpeakerLabel = "Ada" }], 1, 0, 0));

        var (_, stdout, _) = await RunWithAsync(TwoSegments(), step, true, null, "-F", "none", "-m", _model, "-o", Path.Combine(_dir, "one"), clip);

        Assert.Contains("   found 1 speaker (0 to name)", stdout);
    }

    [Fact]
    public async Task NoSpeakersFoundSaysSo()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var step = new FakeSpeakers((_, segs) => new SpeakerOutcome(segs, 0, 0, 0));
        var outDir = Path.Combine(_dir, "none");

        var (code, stdout, _) = await RunWithAsync(TwoSegments(), step, true, null, "-F", "none", "-m", _model, "-o", outDir, clip);

        Assert.Equal(0, code);
        Assert.Contains("   found no speakers", stdout);
        Assert.DoesNotContain("- Speakers:", File.ReadAllText(Path.Combine(outDir, "meeting.md")));
    }

    private static void SeedStaleLabels(string outDir)
    {
        Directory.CreateDirectory(Path.Combine(outDir, "speakers"));
        File.WriteAllText(Path.Combine(outDir, "speakers.json"), "{}");
        File.WriteAllText(Path.Combine(outDir, "speakers", "S1.wav"), "wav");
        File.WriteAllText(Path.Combine(outDir, "speakers", "S12.wav"), "wav");
    }

    [Fact]
    public async Task ATranscriptWithoutLabelsRemovesAnEarlierRunsSpeakerFiles()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var outDir = Path.Combine(_dir, "rerun-off");
        SeedStaleLabels(outDir);
        var kept = Path.Combine(_dir, "rerun-kept");
        SeedStaleLabels(kept);
        File.WriteAllText(Path.Combine(kept, "speakers", "notes.txt"), "mine");

        Assert.Equal(0, (await RunWithAsync(TwoSegments(), TwoSpeakers(), true, null, "--no-speakers", "-F", "none", "-m", _model, "-o", outDir, clip)).code);
        Assert.Equal(0, (await RunWithAsync(TwoSegments(), TwoSpeakers(), true, null, "--no-speakers", "-F", "none", "-m", _model, "-o", kept, clip)).code);

        Assert.False(File.Exists(Path.Combine(outDir, "speakers.json")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "speakers")));
        Assert.False(File.Exists(Path.Combine(kept, "speakers.json")));
        Assert.Equal(["notes.txt"], Directory.GetFiles(Path.Combine(kept, "speakers")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AnEarlierRunsSpeakerFilesAreGoneBeforeTheUnlabelledTranscriptIsWritten()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var outDir = Path.Combine(_dir, "rerun-on");
        SeedStaleLabels(outDir);
        bool? stale = null;
        var step = new FakeSpeakers((dir, segs) =>
        {
            stale = File.Exists(Path.Combine(dir, "speakers.json"));
            throw new InvalidOperationException("native crash stand-in");
        });

        await RunWithAsync(TwoSegments(), step, true, null, "-F", "none", "-m", _model, "-o", outDir, clip);

        Assert.False(stale);
    }

    [Fact]
    public async Task SpeakerFailureStillWritesTheTranscript()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var step = new FakeSpeakers((_, _) => throw new InvalidOperationException("boom"));
        var outDir = Path.Combine(_dir, "boom");

        var (code, _, stderr) = await RunWithAsync(TwoSegments(), step, true, null, "-F", "none", "-m", _model, "-o", outDir, clip);

        Assert.Equal(0, code);
        Assert.Contains("Warning: speaker labels skipped: boom", stderr);
        var md = File.ReadAllText(Path.Combine(outDir, "meeting.md"));
        Assert.Contains("hello there", md);
        Assert.DoesNotContain("- Speakers:", md);
        Assert.DoesNotContain("speaker", File.ReadAllText(Path.Combine(outDir, "transcript.json")));
    }

    [Fact]
    public async Task ConfigFalseTurnsItOff()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var step = TwoSpeakers();

        await RunWithAsync(TwoSegments(), step, false, null, "-F", "none", "-m", _model, "-o", Path.Combine(_dir, "cfgoff"), clip);

        Assert.Equal(0, step.Calls);
    }

    [Fact]
    public async Task FlagBeatsConfig()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var step = TwoSpeakers();

        await RunWithAsync(TwoSegments(), step, false, null, "--speakers", "-F", "none", "-m", _model, "-o", Path.Combine(_dir, "flagon"), clip);

        Assert.Equal(1, step.Calls);
    }

    [Fact]
    public async Task MarkdownOnlyIsLabelledBeforeItIsMoved()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var cwd = Path.Combine(_dir, "cwd");
        Directory.CreateDirectory(cwd);

        var (code, _, _) = await RunWithAsync(TwoSegments(), TwoSpeakers(), true, cwd, "-M", "-m", _model, clip);

        Assert.Equal(0, code);
        Assert.Contains("- Speakers: ", File.ReadAllText(Path.Combine(cwd, "clip.md")));
    }

    private Task<(int code, string stdout, string stderr)> RunAsync(IWhisper w, params string[] args) =>
        RunInAsync(w, null, args);

    private async Task<(int code, string stdout, string stderr)> RunInAsync(IWhisper w, string? cwd, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await TranscribeCommand.RunAsync(args, w, o, e, Path.Combine(_dir, "models"),
            TestContext.Current.CancellationToken, currentDirectory: cwd);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Command_WithFakeWhisper_WritesAllOutputs()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var outDir = Path.Combine(_dir, "o'ut dir,x");
        var w = new FakeWhisper(
            new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(2), " hello there"),
            new Segment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), " bye"));

        var (code, stdout, stderr) = await RunAsync(w, "-F", "changes", "-D", "8", "-m", _model, "-o", outDir, clip);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        foreach (var f in new[] { "audio.wav", "transcript.json", "transcript.srt", "meeting.md" })
            Assert.True(File.Exists(Path.Combine(outDir, f)), f);
        var frames = Directory.GetFiles(Path.Combine(outDir, "frames"), "frame_*.jpg");
        Assert.NotEmpty(frames);
        Assert.Empty(Directory.GetFiles(Path.Combine(outDir, "frames"), "raw_*"));
        var md = File.ReadAllText(Path.Combine(outDir, "meeting.md"));
        Assert.StartsWith("# Meeting: clip\n", md);
        Assert.Contains("hello there", md);
        Assert.Equal(["en", _model, "0"], [w.Language, w.Model, w.MaxContext.ToString()]);
        Assert.Contains("==> Extracting audio to ", stdout);
        Assert.Contains("==> Building meeting.md", stdout);
        Assert.Contains("    Read:   " + Path.Combine(outDir, "meeting.md"), stdout);
    }

    [Fact]
    public async Task Command_MarkdownOnly_WritesStemMdInCurrentDirectory()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var spaced = Path.Combine(_dir, "my clip.mp4");
        File.Move(clip, spaced);
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        var w = new FakeWhisper(new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(1), " just words"));
        var (code, stdout, _) = await RunInAsync(w, work, "-M", "-m", _model, spaced);
        Assert.Equal(0, code);
        Assert.Equal(["my_clip.md"], Directory.GetFileSystemEntries(work).Select(Path.GetFileName).ToArray());
        var md = File.ReadAllText(Path.Combine(work, "my_clip.md"));
        Assert.Contains("> [00:00:00 → 00:00:01] just words", md);
        Assert.DoesNotContain("frames", md);
        Assert.Contains("    Output: my_clip.md", stdout);
        Assert.False(Directory.Exists(Path.GetDirectoryName(w.Wav)!));
    }

    [Fact]
    public async Task Command_MarkdownOnly_WhisperFails_CleansTempDir()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var w = new FakeWhisper { Throw = new InvalidDataException("bad model file") };
        var (code, _, stderr) = await RunInAsync(w, _dir, "-M", "-m", _model, clip);
        Assert.Equal(1, code);
        Assert.Contains("Error: transcription failed", stderr);
        Assert.Contains("bad model file", stderr);
        Assert.NotNull(w.Wav);
        Assert.False(Directory.Exists(Path.GetDirectoryName(w.Wav)!));
        Assert.False(File.Exists(Path.Combine(_dir, "clip.md")));
    }

    [Fact]
    public async Task Command_WhisperThrows_Exit1_NoStackTrace()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var w = new FakeWhisper { Throw = new InvalidOperationException("model load failed") };
        var (code, _, stderr) = await RunInAsync(w, null, "-F", "none", "-m", _model, "-o", Path.Combine(_dir, "o"), clip);
        Assert.Equal(1, code);
        Assert.Contains("Error: transcription failed", stderr);
        Assert.Contains("model load failed", stderr);
        Assert.DoesNotContain("   at ", stderr);
    }

    [Fact]
    public async Task Command_DownloadTimeout_Exit1_RemovesPartFile()
    {
        var input = Path.Combine(_dir, "in.mp4");
        File.WriteAllText(input, "x");
        var models = Path.Combine(_dir, "models");
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await TranscribeCommand.RunAsync([input], new FakeWhisper(), o, e, models,
            TestContext.Current.CancellationToken,
            downloadModel: async (dst, _) =>
            {
                await dst.WriteAsync(new byte[] { 1, 2, 3 });
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
            });
        Assert.Equal(1, code);
        var model = Path.Combine(models, TranscribeCommand.DefaultModelFile);
        Assert.StartsWith($"Error: whisper model not found: {model}", e.ToString());
        Assert.Contains("HttpClient.Timeout", e.ToString());
        Assert.Empty(Directory.GetFiles(models));
    }

    [Fact]
    public async Task Command_DownloadSucceeds_RenamesPartToModel()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await MakeClipAsync();
        var models = Path.Combine(_dir, "models");
        var w = new FakeWhisper();
        var code = await TranscribeCommand.RunAsync(["-F", "none", "-o", Path.Combine(_dir, "o"), clip], w,
            new StringWriter(), new StringWriter(), models, TestContext.Current.CancellationToken,
            downloadModel: (dst, _) => dst.WriteAsync(new byte[] { 1 }).AsTask());
        Assert.Equal(0, code);
        Assert.Equal([TranscribeCommand.DefaultModelFile], Directory.GetFiles(models).Select(Path.GetFileName).ToArray());
        Assert.Equal(Path.Combine(models, TranscribeCommand.DefaultModelFile), w.Model);
    }

    [Fact]
    public async Task Command_MissingInput_Exit1_Message()
    {
        var w = new FakeWhisper();
        var missing = Path.Combine(_dir, "nope.mp4");
        var (code, _, stderr) = await RunAsync(w, "-m", _model, missing);
        Assert.Equal(1, code);
        Assert.Equal($"Error: input file not found: {missing}\n", stderr.ReplaceLineEndings("\n"));
        Assert.False(w.Called);
    }

    [Fact]
    public async Task Command_MissingExplicitModel_Exit1_NoDownload()
    {
        var input = Path.Combine(_dir, "in.mp4");
        File.WriteAllText(input, "x");
        var model = Path.Combine(_dir, "absent.bin");
        var (code, _, stderr) = await RunAsync(new FakeWhisper(), "-m", model, input);
        Assert.Equal(1, code);
        Assert.StartsWith($"Error: whisper model not found: {model}", stderr);
        Assert.False(File.Exists(model));
    }

    [Fact]
    public async Task Command_BadFramesMode_Exit2()
    {
        var (code, _, stderr) = await RunAsync(new FakeWhisper(), "-F", "bogus", "x.mp4");
        Assert.Equal(2, code);
        Assert.StartsWith("Error: invalid --frames mode: bogus", stderr);
        Assert.Contains("Usage: ", stderr);
    }

    [Fact]
    public async Task Command_Help_PrintsUsageToStdout_Exit0()
    {
        var (code, stdout, stderr) = await RunAsync(new FakeWhisper(), "--help");
        Assert.Equal(0, code);
        Assert.StartsWith("Usage: ", stdout);
        Assert.Contains("-M, --markdown-only", stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task Command_HelpAsOptionValue_IsNotHelp()
    {
        var w = new FakeWhisper();
        var (code, stdout, _) = await RunAsync(w, "-o", "-h", Path.Combine(_dir, "missing.mp4"));
        Assert.Equal(1, code);
        Assert.DoesNotContain("Usage:", stdout);
    }

    [Fact]
    public async Task Command_HelpAfterDoubleDash_IsAFileName()
    {
        var (code, stdout, stderr) = await RunAsync(new FakeWhisper(), "--", "-h");
        Assert.Equal(1, code);
        Assert.Equal("Error: input file not found: -h\n", stderr.ReplaceLineEndings("\n"));
        Assert.Equal("", stdout);
    }
}
