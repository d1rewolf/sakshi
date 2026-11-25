# Speaker labels — implementation plan (phase 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finished transcripts say who spoke each line; the user names voices with `sakshi speakers`, and Sakshi suggests those names in later meetings.

**Architecture:** Whisper.net keeps writing the words, now with token times. A new project, `Sakshi.Speakers`, runs sherpa-onnx speaker diarization on the same `audio.wav`, turns pieces into voiceprints, joins and matches them, and gives every word, then every segment, a speaker. `Sakshi.Transcribe` defines `ISpeakerStep` and renders the labels; `Sakshi.Speakers` implements it and the `speakers`/`voices` commands; `Sakshi.App` wires them.

**Tech Stack:** .NET 10, C#, xUnit v3, Whisper.net 1.9.1, org.k2fsa.sherpa.onnx 2.0.3, System.Text.Json.

**Spec:** `docs/design/speakers.md`

## Global Constraints

- Build with `dotnet build -warnaserror` from the repo root; it must stay warning-free.
- Run tests only through the guarded runner, never plain `dotnet test`: `/tmp/claude-1000/-home-jb-workspace-greatworx-sakshi/08d6ae44-7699-4790-9470-bc95314ff7d7/scratchpad/safe-test.sh [args]` (it stubs GUI programs and hides the display so no test can reach the desktop). Example: `.../safe-test.sh tests/Sakshi.Speakers.Tests`. Never start the tray, OBS, a menu or a notifier by hand.
- With speaker labels off (`--no-speakers`, config `"speakers": false`, or no `ISpeakerStep` passed), transcript.json, transcript.srt and meeting.md are byte-for-byte what they are today. All existing tests keep passing unchanged except where a task says to update a fake's signature.
- Speaker labelling never costs a transcript: any failure in it (models, sherpa, library) leaves a transcript without labels plus one `Warning: speaker labels skipped: <reason>` line.
- Constants exactly as in the spec table: `ClusterThreshold` 0.4, `MinTurnForPrint` 1.5 s, `MaxPrintSpeech` 60 s, `MinPrintSpeech` 3 s, `JoinThreshold` 0.90, `SuggestThreshold` 0.60, `Margin` 0.05, `SnapWords` 4, `PauseSeconds` 0.6, `ClipSeconds` 5, max 10 voiceprints per person, seed background count 40.
- Labels: named → the name; suggested → the suggestion + `?`; unknown → `Speaker N` where N is the number in the id (`S3` → `Speaker 3`).
- Every file Sakshi rewrites in a meeting folder or the voice library is written to a temp file in the same folder and renamed over the old one.
- The voice library is `<DataDir>/voices/voices.json`; on Linux/macOS the folder is mode 700 and the file 600.
- Never write a voice library file that could not be read.
- No real names, meetings or voices in code, tests or docs: use made-up names (Ada, Ben, Cy) and synthetic vectors.
- Commit messages follow the repo: `Area: what changed` in plain words (e.g. `Speakers: words to speakers, with boundaries moved to sentence ends`), ending with the two trailer lines:
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_01A2rbJkxgfqTqADRuT8ii3k`.
- Commit on `main`; never push.

## Review Focus

1. A meeting where diarization finds no turns (silence, a 10-second test) — transcript exactly as without labels, no `- Speakers:` line, no speakers.json. Test in Task 7.
2. Speaker models cannot be downloaded, or a download's SHA-256 is wrong — no model file left behind (`.part` removed), transcript written, one warning. Test in Task 6 and Task 8.
3. A voice library file with bad JSON — labelling runs as if empty and does not rewrite it; `sakshi speakers MEETING S1=Ada` and `sakshi voices` exit 1 naming the file, and nothing in the meeting changes. Tests in Task 5 and Task 9.
4. Relabelling a meeting with missing or hand-broken files (no speakers.json, no transcript.json, an unknown id `S9`) — clear message, exit code ≠ 0, no file in the meeting changed. Test in Task 9.
5. Names typed with spaces, mixed case, surrounding spaces or markdown characters (`S2= mary *ann* `, `s2=Ada`) — trimmed, matched case-insensitively against the library and ids, and escaped in meeting.md. Tests in Task 2 and Task 9.

---

### Task 1: Word times from Whisper

**Files:**
- Create: `src/Sakshi.Transcribe/Word.cs`, `src/Sakshi.Transcribe/WordTimes.cs`
- Modify: `src/Sakshi.Transcribe/Segment.cs`, `src/Sakshi.Transcribe/IWhisper.cs`, `src/Sakshi.Transcribe/WhisperNet.cs`, every `IWhisper` fake under `tests/`
- Test: `tests/Sakshi.Transcribe.Tests/WordTimesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record Word(TimeSpan Start, TimeSpan End, string Text);` — `Text` keeps Whisper's leading space.
  - `Segment` keeps its positional constructor and gains `public IReadOnlyList<Word> Words { get; init; } = [];`, `public string? Speaker { get; init; }`, `public string? SpeakerLabel { get; init; }`.
  - `public static IReadOnlyList<Word> WordTimes.FromTokens(IEnumerable<(string Text, long Start, long End)> tokens)` — times in units of 10 ms.
  - `IWhisper.TranscribeAsync(string wavPath, string modelPath, string language, int maxContext, CancellationToken ct, bool words = false)`.

- [ ] **Step 1: Write the failing tests** in `WordTimesTests`:
  - `ControlTokensAreDropped`: tokens `("[_BEG_]",0,0)`, `(" Hi",0,40)`, `("[_TT_150]",150,150)` → one word `" Hi"`, 0 s–0.4 s.
  - `TokensWithoutLeadingSpaceJoinTheWordBefore`: `(" Hel",10,20)`, `("lo",20,35)`, `(",",35,36)`, `(" you",40,60)` → `" Hello,"` 0.10–0.36 s and `" you"` 0.40–0.60 s.
  - `FirstTokenWithoutSpaceStartsAWord`: `("Hi",0,10)` → one word `"Hi"`.
  - `EmptyInputGivesNoWords`.
- [ ] **Step 2:** Run `safe-test.sh tests/Sakshi.Transcribe.Tests` — the new tests fail to compile.
- [ ] **Step 3:** Implement `Word`, `WordTimes.FromTokens` and the `Segment` properties. In `WhisperNet`, when `words` is true call `builder.WithTokenTimestamps()` and set each segment's `Words` from `s.Tokens` (`WhisperToken` fields `Text`, `Start`, `End`). Without `words`, nothing changes. Add the `words` parameter to `IWhisper` and every fake (fakes ignore it unless a later task needs them to return words).
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Transcribe.Tests` and `dotnet build -warnaserror` — all pass.
- [ ] **Step 5: Commit** `Transcribe: words with times from Whisper's tokens`.

### Task 2: Transcript files carry speakers

**Files:**
- Modify: `src/Sakshi.Transcribe/TranscriptFiles.cs`, `src/Sakshi.Transcribe/MeetingMarkdown.cs`
- Test: `tests/Sakshi.Transcribe.Tests/TranscriptFilesTests.cs`, `tests/Sakshi.Transcribe.Tests/MeetingMarkdownTests.cs`

**Interfaces:**
- Consumes: `Segment.Speaker`, `Segment.SpeakerLabel` (Task 1).
- Produces:
  - `TranscriptFiles.WriteJson` writes `"speaker"` and `"speaker_label"` after `"text"` when `Speaker` is not null.
  - `public static (IReadOnlyList<Segment> Segments, string Language) TranscriptFiles.ReadJson(string path)` — reads offsets (ms) into Start/End, `text` as written, and the speaker fields when present.
  - `TranscriptFiles.WriteSrt` prefixes the label: cue text `" " + label + ": " + text.TrimStart()`.
  - `MeetingMarkdown.Render` unchanged signature; uses `SpeakerLabel`.
  - `public static string MeetingMarkdown.EscapeLabel(string label)`.

- [ ] **Step 1: Write the failing tests:**
  - `JsonWithSpeakersHasSpeakerFields`: a segment with `Speaker = "S2", SpeakerLabel = "Ada"` → item has `"speaker": "S2"`, `"speaker_label": "Ada"`, after `"text"`.
  - `JsonRoundTrips`: `WriteJson` → `ReadJson` → `WriteJson` gives identical bytes, with and without speakers; language comes back.
  - `SrtPrefixesTheLabel`: cue text line is ` Ada: Hello there`.
  - `MarkdownShowsLabelWhenSpeakerChanges`: segments Ada, Ada, Ben under one frame → lines `> [..] **Ada:** one`, `> [..] two`, `> [..] **Ben:** three`.
  - `MarkdownRepeatsLabelAfterEachHeading`: Ada before and after a frame heading → both first lines carry `**Ada:**`; also the pre-frame block and the `noFrames` list each start fresh.
  - `MarkdownListsSpeakers`: header has `- Speakers: Ada, Ben?, Speaker 3` right after the `- Frames:`/`- Segments:` line, in order of first appearance.
  - `MarkdownEscapesLabels`: label `mary *ann*_[x]` renders as `**mary \*ann\*\_\[x\]:**`.
  - Existing tests (no speakers) stay unchanged and pass.
- [ ] **Step 2:** Run `safe-test.sh tests/Sakshi.Transcribe.Tests` — new tests fail.
- [ ] **Step 3:** Implement. `EscapeLabel` backslash-escapes `\ * _ [ ] ` and backtick.
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Transcribe.Tests` — all pass.
- [ ] **Step 5: Commit** `Transcribe: transcript files and meeting.md show speakers`.

### Task 3: The Speakers project, and words to speakers

**Files:**
- Create: `src/Sakshi.Speakers/Sakshi.Speakers.csproj` (refs `org.k2fsa.sherpa.onnx` 2.0.3 and `Sakshi.Transcribe`; embeds `background.json`, already in the folder, as resource `Sakshi.Speakers.background.json`), `src/Sakshi.Speakers/SpeakerSettings.cs`, `src/Sakshi.Speakers/SpeakerTurn.cs`, `src/Sakshi.Speakers/WordAligner.cs`, `src/Sakshi.Speakers/SegmentSplitter.cs`
- Create: `tests/Sakshi.Speakers.Tests/Sakshi.Speakers.Tests.csproj` (same shape as `tests/Sakshi.Transcribe.Tests`), `WordAlignerTests.cs`, `SegmentSplitterTests.cs`
- Modify: `Sakshi.slnx` (add both projects)

**Interfaces:**
- Produces:
  - `public static class SpeakerSettings` with every constant from Global Constraints as `public const` (`double`/`int`), named as in the spec table, plus `MaxVoiceprintsPerPerson = 10`, `SeedCount = 40`, `SampleChars = 120`.
  - `public sealed record SpeakerTurn(double Start, double End, string Speaker);` (seconds)
  - `public static string[] WordAligner.Assign(IReadOnlyList<Word> words, IReadOnlyList<SpeakerTurn> turns)` — `[]`-length-matching array; empty turns → all null.
  - `public static string[] WordAligner.Snap(IReadOnlyList<Word> words, IReadOnlyList<string> speakers)`
  - `public static IReadOnlyList<Segment> SegmentSplitter.Split(IReadOnlyList<Segment> segments, IReadOnlyList<string> wordSpeakers)` — `wordSpeakers` indexes all segments' `Words` in order; output segments have `Speaker` set (no label yet) and their own `Words`.

- [ ] **Step 1: Write the failing tests:**
  - `Assign`: a word whose midpoint is inside a turn gets that turn's speaker; a word 0.5 s from the nearest turn gets that turn; a word 2 s from any turn gets the previous word's speaker; the first word with no turn within 1 s gets the next assigned word's speaker.
  - `Snap`: speakers `A, A, B, B` over `" I"`, `" think"`, `" so."`, `" Okay"` (the change at index 2 is inside "I think so.") → `A, A, A, B`.
  - `Snap` with a 0.8 s gap before index 3 counts as a sentence end.
  - `Snap` leaves a change alone when no sentence end lies within 4 words.
  - `Snap` with all one speaker returns the same array.
  - `Split`: one segment of words A,A,B,B → two segments; first keeps the segment's Start and ends at word 2's End; second starts at word 3's Start and keeps the segment's End; texts are the words joined; each has its `Speaker`.
  - `Split`: a segment with no words takes the previous segment's speaker (the first such takes the next one's).
- [ ] **Step 2:** Run `safe-test.sh tests/Sakshi.Speakers.Tests` — fails.
- [ ] **Step 3:** Implement. Snap: for each index i where `speakers[i] != speakers[i-1]` and i is not a sentence start (word i-1 ends in `.`, `?`, `!`, or `words[i].Start - words[i-1].End > PauseSeconds`), find the nearest sentence start j with `|j - i| <= SnapWords` (prefer the earlier one on a tie) and move the change to j; continue scanning after `max(i, j)`.
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Speakers.Tests` and `dotnet build -warnaserror` — pass.
- [ ] **Step 5: Commit** `Speakers: new project; words to speakers, with boundaries moved to sentence ends`.

### Task 4: Voiceprint maths: background, joining, suggestions

**Files:**
- Create: `src/Sakshi.Speakers/Voiceprint.cs`, `src/Sakshi.Speakers/Background.cs`, `src/Sakshi.Speakers/PieceJoiner.cs`, `src/Sakshi.Speakers/Suggester.cs`
- Test: `tests/Sakshi.Speakers.Tests/VoiceprintTests.cs`, `PieceJoinerTests.cs`, `SuggesterTests.cs`

**Interfaces:**
- Produces:
  - `public static float[] Voiceprint.Normalize(float[] raw, float[] mean)` — subtract, scale to length 1; different lengths throw `ArgumentException`; a zero vector stays zero.
  - `public static double Voiceprint.Cosine(float[] a, float[] b)` — dot product of normalised vectors.
  - `public sealed record Background(float[] Mean, int Count)`; `public static Background Background.Seed()` reads the embedded resource; `public static float[] Background.Combine(Background seed, double[]? sum, int count)` → `(seed.Mean × seed.Count + sum) / (seed.Count + count)`.
  - `public static int[] PieceJoiner.Join(IReadOnlyList<float[]> normalized, double threshold)` — group index per input; groups numbered in order of their lowest member.
  - `public sealed record Candidate(string PersonId, string Name, IReadOnlyList<float[]> Prints);` (normalised prints)
  - `public sealed record Suggestion(string PersonId, string Name, double Score);`
  - `public static Suggestion?[] Suggester.Suggest(IReadOnlyList<float[]> speakers, IReadOnlyList<Candidate> people)`

- [ ] **Step 1: Write the failing tests:**
  - `Seed` has 256 values and count 40.
  - `Combine` with seed mean `[1,1]` count 40, sum `[10,0]` count 10 → `[1.0, 0.8]`.
  - `Normalize` of `[3,4]` minus `[0,0]` → `[0.6,0.8]`.
  - `Join`: three vectors where 0–1 cosine 0.95, 2 far from both → `[0,0,1]`; with threshold 0.99 → `[0,1,2]`; average linkage: A–B 0.95, B–C 0.95, A–C 0.5 at threshold 0.9 → A,B joined, C alone (mean of C to {A,B} is 0.725).
  - `Suggest`: best person ≥ 0.60 with margin ≥ 0.05 → suggestion; best 0.70 and second 0.67 → null; best 0.55 → null; two speakers whose best is the same person → only the higher score keeps it, the other gets its next-best person if that still passes, else null.
- [ ] **Step 2:** Run the tests — fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Speakers.Tests` — pass.
- [ ] **Step 5: Commit** `Speakers: voiceprint background, joining pieces and suggesting names`.

### Task 5: Voice library, speakers.json and clips

**Files:**
- Create: `src/Sakshi.Speakers/VoiceLibrary.cs`, `src/Sakshi.Speakers/SpeakersFile.cs`, `src/Sakshi.Speakers/WavFile.cs`, `src/Sakshi.Speakers/AtomicFile.cs`
- Modify: `src/Sakshi.Core/Paths/SakshiPaths.cs` (add `VoicesFile`)
- Test: `tests/Sakshi.Speakers.Tests/VoiceLibraryTests.cs`, `SpeakersFileTests.cs`, `WavFileTests.cs`

**Interfaces:**
- Produces:
  - `SakshiPaths.VoicesFile => Path.Combine(DataDir, "voices", "voices.json")`.
  - `public static void AtomicFile.WriteAllText(string path, string text)` (UTF-8 no BOM, temp file + rename, creates the folder).
  - `public sealed class VoiceLibrary`: `static VoiceLibrary Load(string path)` (missing file → empty; unreadable → throws `VoiceLibraryException` with the path in the message); `IReadOnlyList<Person> People`; `Person? Find(string name)` (case-insensitive); `Person Teach(string name, float[] raw, string meeting, string speaker, DateTimeOffset now)` (finds or creates; keeps the 10 newest prints); `void Rename(string oldName, string newName)` (throws `VoiceLibraryException` if old is missing or new is another person); `bool Forget(string name)`; `void AddToBackground(IEnumerable<float[]> raw)`; `(double[]? Sum, int Count) BackgroundSum`; `void Save()` (atomic; Unix modes 700/600).
  - `public sealed record Person(string Id, string Name, IReadOnlyList<StoredPrint> Prints);` `public sealed record StoredPrint(float[] Vector, string Meeting, string Speaker, DateTimeOffset Added);`
  - `public enum SpeakerStatus { Named, Suggested, Unknown }` serialised lowercase.
  - `public sealed record SpeakerEntry(string Id, SpeakerStatus Status, string? Name, string? PersonId, string? Suggestion, double? Score, double Seconds, string Sample, string Clip, float[] Voiceprint)` with `string Label` (Global Constraints rule).
  - `public sealed record SpeakersFile(int Version, string EmbeddingModel, string Source, IReadOnlyList<FrameRecord>? Frames, IReadOnlyList<SpeakerEntry> Speakers)`; `static SpeakersFile Read(string path)` (throws `InvalidDataException` with the path on bad content); `void Write(string path)` (atomic). JSON names as in the spec (camelCase; frames as `{seconds, fileName}`).
  - `public static (int SampleRate, float[] Samples) WavFile.Read(string path)` (16-bit PCM; mono or first channel); `public static void WavFile.Write(string path, int sampleRate, ReadOnlySpan<float> samples)`.

- [ ] **Step 1: Write the failing tests:**
  - Library: `Load` of a missing file is empty; Teach then Save then Load round-trips; Teach `"ada"` after `"Ada"` adds to the same person and keeps `"Ada"`; the 11th print drops the oldest; Rename to an existing other person throws; Forget removes; bad JSON → `VoiceLibraryException` and the file's bytes are unchanged; on Unix the saved file's mode is 600 and folder 700.
  - Speakers file: write/read round-trip with a named, a suggested and an unknown speaker; labels `Ada`, `Ben?`, `Speaker 3`; status serialised `"named"`; `Frames: null` round-trips as `null`.
  - Wav: write 1 s of a 440 Hz tone at 16 kHz, read back, samples equal within 1/32768.
- [ ] **Step 2:** Run — fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Speakers.Tests tests/Sakshi.Core.Tests` — pass.
- [ ] **Step 5: Commit** `Speakers: voice library, speakers.json and sound clips`.

### Task 6: sherpa-onnx and the speaker models

**Files:**
- Create: `src/Sakshi.Speakers/IDiarizer.cs`, `src/Sakshi.Speakers/IVoiceprinter.cs`, `src/Sakshi.Speakers/SherpaDiarizer.cs`, `src/Sakshi.Speakers/SherpaVoiceprinter.cs`, `src/Sakshi.Speakers/SpeakerModels.cs`
- Test: `tests/Sakshi.Speakers.Tests/SpeakerModelsTests.cs`, `SherpaIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PieceTurn(double Start, double End, int Piece);`
  - `public interface IDiarizer : IDisposable { IReadOnlyList<PieceTurn> Diarize(float[] samples, CancellationToken ct); }` (16 kHz input)
  - `public interface IVoiceprinter : IDisposable { string ModelName { get; } float[] Compute(float[] samples, IReadOnlyList<(double Start, double End)> spans); }`
  - `SherpaDiarizer(string segmentationModel, string embeddingModel, int threads)` — `Clustering.Threshold = SpeakerSettings.ClusterThreshold`.
  - `SherpaVoiceprinter(string embeddingModel, int threads)` — one stream fed with every span's samples, then `InputFinished`, then `Compute`.
  - `public sealed record SpeakerModelPaths(string Segmentation, string Embedding)`; `static SpeakerModelPaths SpeakerModels.In(string modelsDir)` → `<modelsDir>/speakers/pyannote-segmentation-3-0.onnx` and `.../wespeaker_en_voxceleb_resnet34_LM.onnx`.
  - `static Task<string?> SpeakerModels.EnsureAsync(string modelsDir, TextWriter stdout, CancellationToken ct, Func<Uri, Stream, CancellationToken, Task>? download = null)` → null when both files are present and correct, else the reason. URLs and SHA-256 exactly as in the spec. Each download goes to `<file>.part`, is hashed, and is renamed only when the hash matches; any failure deletes the `.part`. Prints `==> Downloading speaker model NAME to PATH`. An existing file is trusted (not re-hashed) so starting is fast.

- [ ] **Step 1: Write the failing tests:**
  - `EnsureDownloadsBothAndChecksHashes`: fake `download` writes bytes whose SHA-256 is wrong → returns a reason containing `SHA-256`, no `.part` and no model file remain.
  - `EnsureReturnsNullWhenFilesExist`: both files present → null, `download` never called.
  - `EnsureReportsDownloadFailure`: `download` throws `HttpRequestException("offline")` → reason contains `offline`, no `.part` left.
  - `SherpaIntegrationTests.LabelsARealFile`: skipped unless env `SAKSHI_SPEAKER_MODELS` (a folder with both model files) and `SAKSHI_SPEAKER_WAV` (a 16 kHz mono WAV) are set; then `Diarize` returns at least one turn and `Compute` returns 256 floats. Use xUnit v3's `Assert.Skip` when unset.
- [ ] **Step 2:** Run — fail.
- [ ] **Step 3:** Implement (the hash check is testable by passing a different expected hash table into an internal overload; the public method uses the spec's hashes).
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Speakers.Tests` — pass (integration test skipped).
- [ ] **Step 5: Commit** `Speakers: sherpa-onnx diarization and voiceprints; model download with pinned hashes`.

### Task 7: The labeller

**Files:**
- Create: `src/Sakshi.Transcribe/ISpeakerStep.cs`, `src/Sakshi.Speakers/SpeakerLabeller.cs`, `src/Sakshi.Speakers/SpeakerStep.cs`
- Test: `tests/Sakshi.Speakers.Tests/SpeakerLabellerTests.cs`

**Interfaces:**
- Consumes: Tasks 3–6.
- Produces:
  - In Transcribe: `public sealed record SpeakerOutcome(IReadOnlyList<Segment> Segments, int Speakers, int Suggested, int Unknown);` and `public interface ISpeakerStep { Task<SpeakerOutcome> LabelAsync(string audioWav, string outputDir, IReadOnlyList<Segment> segments, string source, IReadOnlyList<FrameRecord>? frames, TextWriter stdout, CancellationToken ct); }`. `Speakers == 0` means no labels: `Segments` is the input unchanged.
  - `public sealed class SpeakerLabeller(IDiarizer diarizer, IVoiceprinter printer, VoiceLibrary library, bool libraryWritable)` with `SpeakerOutcome Label(float[] samples, string outputDir, string meetingKey, IReadOnlyList<Segment> segments, string source, IReadOnlyList<FrameRecord>? frames, CancellationToken ct)`; it writes `speakers.json` and `speakers/Sn.wav` into `outputDir`, adds every piece's raw voiceprint to the library background and saves the library only when `libraryWritable`.
  - `public sealed class SpeakerStep(string modelsDir, string voicesFile, int threads) : ISpeakerStep` — ensures models, loads the library (an unreadable one → empty and not writable, with a warning line), reads `audioWav` with `WavFile`, builds sherpa objects, runs `SpeakerLabeller`. `meetingKey` = the last two path parts of `outputDir` joined with `/`.

- [ ] **Step 1: Write the failing tests** (fake `IDiarizer` returning fixed `PieceTurn`s and fake `IVoiceprinter` returning a fixed vector per piece, identified by the first span's start; segments with `Words`):
  - `NoTurnsMeansNoLabels`: diarizer returns nothing → `Speakers == 0`, segments unchanged, no speakers.json written.
  - `PiecesWithSimilarPrintsAreJoined`: pieces 0 and 2 have cosine ≥ 0.9 after normalisation → one speaker id; 3 pieces → 2 speakers `S1`, `S2` numbered by first appearance.
  - `ShortPieceIsGivenToTheTurnBefore`: a piece with 2 s of speech gets no voiceprint and its words take the previous turn's speaker.
  - `SuggestionFromTheLibrary`: library has Ada with the same vector as piece 0 → `S1` suggested `Ada` with score ≈ 1, label `Ada?`, `Suggested == 1`.
  - `SpeakersFileAndClipsAreWritten`: speakers.json lists each speaker with `seconds`, `sample` (longest segment text trimmed, ≤ 120 chars), `clip` `speakers/S1.wav`; the clip exists and is 5 s long (or the whole turn if shorter), cut from the middle of the speaker's longest turn.
  - `BackgroundGrowsOnlyWhenWritable`: with `libraryWritable` false the library file is not created/changed.
  - `PrintsUseOnlyLongTurnsUpTo60Seconds`: the spans passed to the voiceprinter are turns ≥ 1.5 s, longest first, totalling ≤ 60 s (the last may be cut to fit).
- [ ] **Step 2:** Run — fail.
- [ ] **Step 3:** Implement following spec steps 2–10, in this order: diarize; per piece pick spans (`MinTurnForPrint`, `MaxPrintSpeech`, `MinPrintSpeech`); reassign turns of print-less pieces; normalise with `Background.Combine(seed, library sum)`; `PieceJoiner.Join(JoinThreshold)`; number speakers by first turn; `Suggester.Suggest` against the library (each speaker's voiceprint = mean of its pieces' raw prints, then normalised); `WordAligner.Assign` + `Snap`; `SegmentSplitter.Split`; set `SpeakerLabel`; write clips and speakers.json; update background.
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Speakers.Tests` — pass.
- [ ] **Step 5: Commit** `Speakers: label a meeting's segments with speakers`.

### Task 8: Transcribe uses the speaker step

**Files:**
- Modify: `src/Sakshi.Transcribe/TranscribeOptions.cs`, `src/Sakshi.Transcribe/TranscribeUsage.cs`, `src/Sakshi.Transcribe/TranscribeCommand.cs`, `src/Sakshi.Core/Config/SakshiConfig.cs`, `src/Sakshi.App/Commands.cs`, `src/Sakshi.App/Sakshi.App.csproj` (ref Sakshi.Speakers)
- Test: `tests/Sakshi.Transcribe.Tests/OptionsTests.cs`, `TranscribeCommandTests.cs`, `tests/Sakshi.Core.Tests` config tests

**Interfaces:**
- Consumes: `ISpeakerStep`, `SpeakerOutcome` (Task 7), `SpeakerStep` (Task 7).
- Produces:
  - `TranscribeOptions.Speakers` (`bool?`): `--speakers` → true, `--no-speakers` → false.
  - `TranscribeCommand.RunAsync(..., TextWriter? log = null, ISpeakerStep? speakers = null, bool configSpeakers = true)`.
  - `SakshiConfig.Speakers` (`bool`, default `true`, JSON `speakers`).

- [ ] **Step 1: Write the failing tests:**
  - Options: `--speakers`/`--no-speakers` parse; usage text lists both: `--speakers / --no-speakers   label who said what (default: the config's "speakers", on)`.
  - `SpeakersOffLeavesOutputUnchanged`: with a fake step and `--no-speakers`, the step is never called and Whisper is called with `words: false`.
  - `SpeakersOnLabelsTheTranscript`: fake step returns segments with labels → transcript.json has speaker fields, meeting.md has `- Speakers:`, stdout has `==> Finding speakers` and `   found 2 speakers (1 to name)` where "to name" counts suggested + unknown; Whisper was called with `words: true`.
  - `SpeakerFailureStillWritesTheTranscript`: fake step throws `InvalidOperationException("boom")` → exit 0, meeting.md without labels, stderr has `Warning: speaker labels skipped: boom`.
  - `ConfigFalseTurnsItOff` and `FlagBeatsConfig`.
  - Config: `"speakers": false` loads; missing key → true.
- [ ] **Step 2:** Run — fail.
- [ ] **Step 3:** Implement. Order in `RunCoreAsync`: extract audio → Whisper (words when enabled) → frames → speaker step (when enabled) → write transcript.json and transcript.srt → meeting.md. `source` = `Path.GetFileName(o.Input)`, `frames` = null when `noFrames`. In `Commands.TranscribeAsync` pass `new SpeakerStep(paths.ModelsDir, paths.VoicesFile, Math.Max(1, Environment.ProcessorCount / 2))` and `cfg.Speakers`.
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Transcribe.Tests tests/Sakshi.Core.Tests tests/Sakshi.App.Tests` and `dotnet build -warnaserror` — pass.
- [ ] **Step 5: Commit** `Transcribe: label speakers by default; --speakers/--no-speakers and the speakers setting`.

### Task 9: `sakshi speakers` — list, name, join, accept

**Files:**
- Create: `src/Sakshi.Speakers/MeetingLocator.cs`, `src/Sakshi.Speakers/MeetingRewriter.cs`, `src/Sakshi.Speakers/SpeakersCommand.cs`
- Modify: `src/Sakshi.App/Commands.cs` (dispatch `speakers`; top-level help line)
- Test: `tests/Sakshi.Speakers.Tests/SpeakersCommandTests.cs`, `MeetingLocatorTests.cs`

**Interfaces:**
- Produces:
  - `public static string MeetingLocator.Find(string arg, IReadOnlyList<string> roots)` — an existing folder path wins; else folders named `arg` under `root/*/` across roots, newest by its parent folder name (the date) then by mtime; none → `UsageException("no meeting named ARG under ROOTS")`.
  - `public static void MeetingRewriter.Write(string dir, SpeakersFile file, IReadOnlyList<Segment> segments, string language)` — sets every segment's `SpeakerLabel` from `file`, writes transcript.json, transcript.srt, meeting.md (from `file.Source`/`file.Frames`) and speakers.json, each atomically.
  - `public static int SpeakersCommand.Run(IReadOnlyList<string> args, IReadOnlyList<string> roots, string voicesFile, TextWriter stdout, TextWriter stderr, DateTimeOffset now)` (`--add` is Task 10).

- [ ] **Step 1: Write the failing tests** (a temp meeting folder made with `MeetingRewriter.Write` from synthetic segments):
  - List: `sakshi speakers DIR` prints one block per speaker: `S1  Ada          7:12  "Can you see my screen?"` then the clip's full path indented four spaces; suggested shows `Ben? (0.74)`; unknown shows `unknown`; talk time `m:ss`.
  - `S2=Ada` names S2: speakers.json status named, library has Ada with S2's voiceprint, meeting.md shows `**Ada:**`, stdout says `S2 is now Ada`.
  - `s2= mary *ann* ` trims, accepts lowercase id, escapes in meeting.md.
  - Same name on S1 and S3 → S3's segments become S1, S3 removed from speakers.json, S1's seconds summed, stdout `S3 joined S1 (Ada)`.
  - Naming S3 with the name S1 already has → joined the same way.
  - `S2=` → unknown, label `Speaker 2`, library unchanged.
  - `--accept` → every suggested becomes named and is taught.
  - `S9=Ada` → exit 2, `no speaker S9 in DIR (S1–S3)`, no file changed (compare bytes).
  - Meeting without speakers.json → exit 1, `DIR has no speaker labels; run: sakshi speakers --add DIR`.
  - Bad voices.json → exit 1 naming the file, meeting files unchanged.
  - Locator: name lookup under two roots returns the newest.
- [ ] **Step 2:** Run — fail.
- [ ] **Step 3:** Implement. Wire `speakers` in `Commands.cs` with roots `[cfg.MeetingsDir, cfg.EffectiveArchiveDir]` and `paths.VoicesFile`; add `speakers` and `voices` to the top-level help in the same style as `find`.
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Speakers.Tests tests/Sakshi.App.Tests` — pass.
- [ ] **Step 5: Commit** `Speakers: sakshi speakers lists, names and joins a meeting's voices`.

### Task 10: `sakshi speakers --add` and `sakshi voices`

**Files:**
- Create: `src/Sakshi.Speakers/AddSpeakersCommand.cs`, `src/Sakshi.Speakers/VoicesCommand.cs`
- Modify: `src/Sakshi.Speakers/SpeakersCommand.cs`, `src/Sakshi.App/Commands.cs`
- Test: `tests/Sakshi.Speakers.Tests/AddSpeakersCommandTests.cs`, `VoicesCommandTests.cs`

**Interfaces:**
- Consumes: `IWhisper`, `Ffmpeg.ExtractAudioAsync`, `TranscriptFiles.ReadJson`, `ISpeakerStep`, `MeetingRewriter`, `VoiceLibrary`.
- Produces:
  - `public static Task<int> AddSpeakersCommand.RunAsync(IReadOnlyList<string> meetings, IReadOnlyList<string> roots, IWhisper whisper, string model, ISpeakerStep step, TextWriter stdout, TextWriter stderr, CancellationToken ct)`; `SpeakersCommand.Run` hands `--add` to it (make `Run` async: `RunAsync`, same parameters plus the Add dependencies).
  - `public static int VoicesCommand.Run(IReadOnlyList<string> args, string voicesFile, TextWriter stdout, TextWriter stderr)`.

- [ ] **Step 1: Write the failing tests** (fake `IWhisper` returning segments with words; fake `ISpeakerStep` returning labelled segments and writing a speakers.json):
  - `--add DIR` with `audio.wav` present: Whisper called with `words: true` and the language from the existing transcript.json; files rewritten; frames read from `frames/frame_HH-MM-SS[_n].jpg` (seconds from the name) when there is no speakers.json; `source` = the recording's file name; no frames folder → `frames` null.
  - `--add` with no `audio.wav` and no recording → exit 1 `DIR has no audio.wav or recording to label`.
  - `--add` on two meetings runs both and reports each.
  - `voices` lists `Ada  3 voiceprints  2 meetings`; no people → `No voices yet. Name a speaker with: sakshi speakers MEETING S1=NAME`.
  - `voices rename Ada Ann`, `voices forget Ann` (stdout `Forgot Ann and 3 voiceprints`); unknown name → exit 1; bad file → exit 1 naming it.
- [ ] **Step 2:** Run — fail.
- [ ] **Step 3:** Implement; wire `voices` and `speakers --add` in `Commands.cs` (`new WhisperNet()`, the config/default model path as `TranscribeAsync` resolves it, `new SpeakerStep(...)`).
- [ ] **Step 4:** Run `safe-test.sh tests/Sakshi.Speakers.Tests tests/Sakshi.App.Tests` — pass.
- [ ] **Step 5: Commit** `Speakers: label older meetings with --add; sakshi voices lists, renames and forgets`.

### Task 11: Notification, doctor, notices and guide

**Files:**
- Modify: `src/Sakshi.Core/Transcription/TranscriptionQueue.cs`, `src/Sakshi.Platform/Doctor/Doctor.cs`, `THIRD-PARTY-NOTICES.md`, `docs/user-guide.md`, `README.md` (one line in the feature list if it has one)
- Test: `tests/Sakshi.Core.Tests` (queue), `tests/Sakshi.Platform.Tests` (doctor)

**Interfaces:**
- Produces: `public static int SpeakerCount.ToName(string meetingDir)` in `src/Sakshi.Core/Transcription/SpeakerCount.cs` — counts entries in speakers.json whose status is not `named`; 0 when the file is missing or unreadable.

- [ ] **Step 1: Write the failing tests:**
  - Queue: a finished job whose speakers.json has 2 unnamed speakers notifies `Transcript ready: NAME` with body `<meeting.md path> · 2 voices to name`; with none, the body is as today.
  - Doctor: "Speaker models" passes with both files in `<ModelsDir>/speakers/`; otherwise passes with detail `downloaded on first transcript`.
- [ ] **Step 2:** Run — fail.
- [ ] **Step 3:** Implement. Notices: sherpa-onnx (Apache-2.0), ONNX Runtime (MIT), pyannote segmentation 3.0 (MIT), WeSpeaker ResNet34-LM (CC BY 4.0, trained on VoxCeleb), LibriSpeech (CC BY 4.0, background mean), following the file's existing format. User guide: a "Who said what" section in the guide's voice — what it does, reading `Ada`/`Ben?`/`Speaker 3`, naming and joining, `--accept`, `--add` for older meetings, `sakshi voices`, turning it off, and plainly where voiceprints are kept and that `sakshi voices forget` deletes them.
- [ ] **Step 4:** Run `safe-test.sh` (whole suite) and `dotnet build -warnaserror` — pass.
- [ ] **Step 5: Commit** `Speakers: notification counts voices to name; doctor, notices and user guide`.
