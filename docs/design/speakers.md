# Speaker labels

Status: design, approved for phase 1. Phases 2–4 are described so that phase 1 does not paint them into a corner.

## Goal

A finished transcript says who said each line, by name. You name a voice once; Sakshi recognises it in later
meetings. Everything runs on the computer: no audio, transcript or voiceprint leaves it.

## What the user sees

After a meeting, meeting.md reads:

```
- Speakers: Jen, John?, Speaker 3

## [00:02:10]

> [00:02:10 → 00:02:14] **Jen:** Can everyone see my screen?
> [00:02:15 → 00:02:19] **Speaker 3:** Yes.
> [00:02:19 → 00:02:24] **John?:** Okay, so the totals are different on the paper logs.
> [00:02:24 → 00:02:28] The paper log says collect.
```

`Jen` is a name the user gave; `John?` is Sakshi's suggestion from the voice library, not yet confirmed; `Speaker 3`
is a voice nobody has named.

A line starts with the speaker's name in bold when the speaker differs from the line above it (the first line under
each frame heading always has one). The notification says `Transcript ready: NAME` as today, and adds
`2 voices to name` when some speakers have no name.

`sakshi speakers MEETING` lists the voices, a sample line and a short sound clip for each;
`sakshi speakers MEETING S3=Gregory` names one, which rewrites the meeting's files and teaches Sakshi the voice.

## How it works

Whisper still writes every word. Speaker labelling is a separate step on the same `audio.wav`; the two are joined
word by word.

1. **Words with times.** Whisper.net runs as today plus `WithTokenTimestamps()`. This does not change the text or
   the segment times (checked on an 11-minute meeting: 143 segments, identical text and times). Each segment carries
   its tokens (`WhisperToken.Text`, `.Start`, `.End`, in units of 10 ms). Tokens whose text starts with `[_` are
   control tokens and are dropped. A token whose text starts with a space starts a new word; others join the word
   before. A word's time is its first token's start to its last token's end.
2. **Turns.** sherpa-onnx `OfflineSpeakerDiarization` on `audio.wav`: segmentation model pyannote segmentation 3.0,
   embedding model WeSpeaker ResNet34-LM (VoxCeleb), `Clustering.Threshold = 0.4`. A low threshold splits too much on
   purpose: one person may come out as several pieces, but two people are rarely joined.
3. **A voiceprint per piece.** For each piece, the embedding of up to 60 s of its longest turns, using only turns of
   at least 1.5 s. A piece with less than 3 s of such turns gets no voiceprint; its turns are given to the speaker of
   the turn just before (or, for the first turn, just after).
4. **Normalised voiceprints.** Every voiceprint is compared after subtracting a fixed background mean and scaling to
   length 1 (see "Background mean"). Raw WeSpeaker vectors from one call all score 0.75–0.98 against each other and
   cannot be told apart.
5. **Join the pieces.** Average-linkage: repeatedly join the two groups with the highest mean cosine while it is at
   least `JoinThreshold`. The threshold is high on purpose: a person left in two pieces costs one extra name to type;
   two people joined cannot be undone by naming.
6. **Suggest names.** Each speaker's score against a person is the best cosine between the speaker's voiceprint
   and any of that person's stored voiceprints. When the best person scores at least `SuggestThreshold` and at least
   `Margin` above the next person, the speaker is `suggested` with that name, shown as `Jen?`. Otherwise it is
   `unknown`, shown as `Speaker N`. Two speakers are never suggested the same person: the higher score keeps it.
   Sakshi never names a speaker by itself in this phase: the spike could not separate the same person from
   different people reliably enough (see "Evidence"). Names are only ever confirmed by the user, which also gives
   us real data to measure automatic naming against later.
7. **Words to speakers.** A word belongs to the turn that contains its midpoint; a word in no turn goes to the
   nearest turn within 1 s, else to the previous word's speaker.
8. **Clean boundaries.** Where the speaker changes between two words and that point is not a sentence end, the change
   moves to the nearest sentence end within 4 words either side. A sentence end is a word ending in `.`, `?` or `!`,
   or a gap of more than 0.6 s before the next word. With none in reach, the change stays where it is.
9. **Segments.** Whisper's segments are kept. A segment whose words have more than one speaker is split where the
   speaker changes. Each segment gets the speaker of its words.
10. **Numbering.** Speakers are numbered `S1`, `S2`, … in order of first appearance. `Speaker N` uses the same N.

Values, all in one class so they can be tuned together:

| Name | Value | Why |
|---|---|---|
| `ClusterThreshold` | 0.4 | 0.6 joined two people in a one-on-one and two pairs in a ten-person meeting; 0.4 split everyone |
| `MinTurnForPrint` | 1.5 s | |
| `MaxPrintSpeech` | 60 s | |
| `MinPrintSpeech` | 3 s | |
| `JoinThreshold` | 0.90 | no two different people scored above 0.85 with any background tried |
| `SuggestThreshold` | 0.60 | a wrong suggestion costs one correction; a missing one costs typing the name |
| `Margin` | 0.05 | |
| `SnapWords` | 4 | |
| `PauseSeconds` | 0.6 | |
| `ClipSeconds` | 5 | |

## Background mean

Voiceprints are compared after subtracting a background mean. The spike showed the mean matters a great deal and
that a mean of clean read speech alone does not remove what meeting audio has in common (codec, microphones, room):
with it, two different men in the same calls scored 0.84. A mean that includes pieces from the user's own meetings
did much better (different people at most 0.69).

So the background is a running mean:

- **Seed:** Sakshi ships `src/Sakshi.Speakers/background.json`: the mean WeSpeaker voiceprint of LibriSpeech
  dev-clean (CC BY 4.0), one voiceprint from the first 40 s of each of its 40 speakers, with the embedding model's
  name and SHA-256. `tools/speakers/README.md` says how it was made. It holds no recognisable voice. It counts as
  40 voiceprints.
- **Running:** after each labelled meeting, the raw voiceprint of every piece is added to a running sum and count
  kept in the voice library file (`background.sum`, `background.count`). Only the sum is kept, never the pieces.
- The mean used is (seed mean × 40 + sum) ÷ (40 + count).

## Files

In the meeting folder:

- **transcript.json** keeps whisper-cli's layout. Each item gains `"speaker": "S2"` and `"speaker_label"` with what
  meeting.md shows (`Jen`, `John?`, `Speaker 3`). With speaker labels off, the file is exactly as today.
- **transcript.srt**: each cue's text is prefixed with the label: ` Jen: Can everyone see my screen?`.
- **meeting.md**: as above. The `- Speakers:` line follows the `- Frames:`/`- Segments:` line and lists the labels in
  order of first appearance. In a label, the characters `\ * _ [ ] ` + "`" + ` are escaped with a backslash. With
  labels off, exactly as today.
- **speakers.json** (new):

```json
{
  "version": 1,
  "embeddingModel": "wespeaker_en_voxceleb_resnet34_LM",
  "source": "steering.mp4",
  "frames": [ { "seconds": 130.4, "fileName": "frame_00-02-10.jpg" } ],
  "speakers": [
    {
      "id": "S2",
      "status": "named",
      "name": "Jen",
      "personId": "6f1c…",
      "suggestion": null,
      "score": 0.87,
      "seconds": 412.5,
      "sample": "Can everyone see my screen?",
      "clip": "speakers/S2.wav",
      "voiceprint": [0.012, …]
    }
  ]
}
```

`status` is `named` (by the user), `suggested` (`suggestion` and `score` hold the suggestion) or `unknown`.
`personId` is the library person's id: the named person, or the suggested one (so accepting a suggestion teaches that
person even after a `voices rename`); `null` when unknown.
`voiceprint` is the raw vector, so that a later background mean can be applied. `sample` is the speaker's longest
segment text, trimmed and cut to 120 characters. `source` and `frames` are what meeting.md was built from, so it can
be rebuilt exactly; `frames` is `null` when the meeting has no frames section (`-F none` or `--markdown-only`).

- **speakers/S2.wav**: `ClipSeconds` from the middle of the speaker's longest turn, 16 kHz mono, written directly
  from `audio.wav`'s samples.

## Voice library

`<DataDir>/voices/voices.json`:

```json
{
  "version": 1,
  "background": { "sum": [ ... ], "count": 57 },
  "people": [
    { "id": "6f1c…", "name": "Jen",
      "voiceprints": [ { "vector": [...], "meeting": "2026-11-20-Fri/steering", "speaker": "S9", "added": "2026-11-25T10:00:00Z" } ] }
  ]
}
```

- Only the user teaches the library: naming a speaker or accepting a suggestion adds that speaker's voiceprint.
  Suggestions never add one, so a wrong match cannot reinforce itself.
- A voiceprint is stored with its meeting and speaker id. Teaching first removes the same voiceprint already stored
  for the same meeting, from whichever person holds it, so correcting a name (`S1=Ada`, then `S1=Ben`) fixes what was
  learned and naming twice keeps one voiceprint. Taking a name off (`S1=`) removes that one voiceprint. Prints are
  matched on the vector, not the id, because labelling a meeting again (`--add`, or transcribing it again) numbers its
  speakers afresh: the new S1 is not the voice the old S1 taught, and correcting it leaves that earlier voiceprint
  alone, while the same voice coming back under another id still replaces its earlier print. The id is kept only as a
  record. A person left with no voiceprints stays in the library (listed with 0) and is never suggested.
- Joining two speakers replaces the voiceprints both taught with the joined speaker's voiceprint, stored under the id
  that stays, so the library holds exactly the voiceprint speakers.json shows for each named speaker, and correcting
  or taking off the joined speaker's name later takes back everything both pieces taught.
- Labelling adds to the background sum. It reads the library again just before saving and applies only that update,
  so a name taught or a person forgotten meanwhile is kept; if the file cannot be read then, it is left alone.
- If the file exists but cannot be read (bad JSON, wrong version), labelling still runs as if it were empty but
  never writes it, and the `speakers`/`voices` commands stop with an error naming the file. Nothing overwrites a file
  Sakshi could not read.
- Each person keeps at most 10 voiceprints; the oldest goes first.
- Names are matched case-insensitively and kept as first typed.
- Written atomically (temp file, then rename). On Linux and macOS the folder is mode 700 and the file 600.

## Commands

`MEETING` is a meeting folder path, or a folder name looked up under `meetingsDir` and the archive folder (the newest
match wins).

```
sakshi speakers MEETING                 list the speakers
sakshi speakers MEETING S3=Gregory ...  name speakers; the same name on two speakers joins them
sakshi speakers MEETING S3=             take a name off (back to unknown) and forget the voiceprint it taught
sakshi speakers MEETING --accept        accept every suggestion
sakshi speakers --add MEETING ...       label a meeting transcribed without speaker labels
sakshi voices                           list people, with how many voiceprints and meetings each has
sakshi voices rename OLD NEW            rename a person (meetings already written keep the old name until relabelled)
sakshi voices forget NAME               delete a person and all their voiceprints
```

The list shows, per speaker: id, name or `Jen? (0.74)` or `unknown`, talk time, the sample line and the clip's full
path. Naming rewrites transcript.json, transcript.srt, meeting.md and speakers.json in that meeting, and prints what
changed. Each file is written to a temp file and renamed over the old one.

`--add` needs `audio.wav`, or the recording (`*.mp4`, `*.mkv`, `*.m4a`) to extract it from. It runs Whisper again
(with word times) and the speaker steps, and rewrites transcript.json, transcript.srt, meeting.md and speakers.json.
Frames are not extracted again: meeting.md is rebuilt from the `frames/frame_HH-MM-SS*.jpg` files already there.
speakers.json is replaced together with the other three files (all temp files written first, then renamed); the clips
are swapped in afterwards, best effort. A clip that cannot be replaced (on Windows, one open in a player) goes in
under the first free name `S3-2.wav`, `S3-3.wav`, … and speakers.json is pointed at it, so a speaker's clip never
plays someone else; the next labelling removes those names too. If even that fails, Sakshi prints
`Warning: DIR: could not put the new voice clip for S3 in place: …` and that clip may still play an earlier voice. ffmpeg is needed only for a meeting without `audio.wav`; without it, that meeting
is skipped with a warning. On a meeting that already has labels, `--add` starts over: names given before come back as
suggestions if the voices are in the library.

`sakshi transcribe` removes an earlier run's speakers.json and clips before it writes the unlabelled transcript. If
transcript.json carries none of speakers.json's ids, the `speakers` command refuses with
`DIR: the transcript has no speaker labels; run: sakshi speakers --add DIR`.

## Transcribe

- Config `speakers` (bool, default `true`). `sakshi transcribe` takes `--speakers` / `--no-speakers`, which win over
  the config.
- Models live in `<ModelsDir>/speakers/`, downloaded on first use from fixed URLs, checked against a pinned SHA-256,
  written as `.part` then renamed, like the Whisper model:
  - `pyannote-segmentation-3-0.onnx` — `https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx`,
    SHA-256 `220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079`
  - `wespeaker_en_voxceleb_resnet34_LM.onnx` — `https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx`,
    SHA-256 `e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012`
- If the models cannot be had, or labelling fails, the transcript is written without labels and the log says why in
  one line. Speaker labels never cost a transcript.
- The progress lines gain `==> Finding speakers` and `   found N speakers (M to name)` (M counts the suggested and
  unknown speakers; `1 speaker` when there is one, `found no speakers` when there are none).
- Threads: `max(1, ProcessorCount / 2)`, as the live transcript uses.
- The live transcript does not change.

## Doctor and notices

- `sakshi doctor` gains "Speaker models": present, or "downloaded on first transcript".
- The tray's transcription queue counts a run that exits non-zero after writing meeting.md (newer than the run's
  start) as done, since only the speaker step comes after: it notifies `Transcript ready: NAME` with the body
  `<meeting.md path> · speaker labels skipped` and runs the hand-off as usual.
- THIRD-PARTY-NOTICES gains sherpa-onnx (Apache-2.0), ONNX Runtime (MIT), pyannote segmentation 3.0 (MIT),
  WeSpeaker ResNet34-LM (CC BY 4.0, trained on VoxCeleb) and LibriSpeech (CC BY 4.0, for the background mean).
- The user guide gets a "Who said what" section: what it does, naming, `--add`, turning it off, and where voiceprints
  are kept and how to delete them.

## Privacy

A voiceprint identifies a person's voice. They are kept only in the meeting's speakers.json and the voice library on
this computer, are never uploaded, and `sakshi voices forget` deletes a person. The user guide says this plainly.
Deleting a meeting deletes its speakers.json and clips with it.

## Cost

On a 24-core machine with 8 threads: diarization about 12–14× real time (4 minutes for 48 minutes of audio);
Whisper's word times add nothing measurable. Downloads: about 33 MB once. The installer grows by the sherpa-onnx
native runtime: about 22 MB on Windows, 32 MB on Linux and macOS.

## Testing

- Pure steps (words from tokens, words to speakers, boundary snapping, segment splitting, joining, matching,
  numbering, the files, the library, the commands) are unit-tested with made-up vectors and times.
- The sherpa-onnx wrapper sits behind `IDiarizer` and `IVoiceprinter`, as Whisper sits behind `IWhisper`, and is not
  exercised in CI (it needs the models). One integration test runs it when `SAKSHI_SPEAKER_MODELS` names a folder
  holding both models, and is skipped otherwise.
- Real meetings are checked by hand before release.

## Phases

1. **This build:** everything above.
2. **Naming window and automatic names:** the notification opens an Avalonia window listing voices with a play
   button, a name box with suggestions from the library and from names said in the meeting ("Hi, I'm …"). Automatic
   naming is turned on once names users confirmed show where a safe threshold lies.
3. **Separate tracks:** OBS records the microphone and the computer's sound on separate tracks; the microphone track is
   the user, exactly. Needs OBS profile changes on all three systems and MacMeetingAudio on macOS.
4. **Live labels and search:** speaker names in the live transcript; `sakshi find` shows and filters by speaker.

## Evidence

From a spike on three recorded meetings (2, 2 and about 10 people; one mixed track each), using the models above.

- One threshold does not fit all meetings: 0.6 was right for one two-person call, joined both people of another, and
  joined two pairs in the ten-person meeting. 0.4 separated everyone and split some people into 2–3 pieces.
- The background mean decides how well voices separate. With a mean of the three meetings' own pieces, different
  people scored at most 0.69 against each other, but some pieces of the same person scored as low as 0.47 (short,
  mixed pieces); with the LibriSpeech mean alone, different men scored up to 0.85. No fixed threshold separated
  same from different with these pieces, so this phase suggests and never names on its own. Score normalisation
  against a LibriSpeech cohort (S-norm, AS-norm) did not separate them either.
- Clear cases did work: one person's voice matched across all three meetings at 0.90–0.97, and another's at 0.87–0.90.
- Word-level assignment matters: with Whisper's segments alone, about one segment in seven held two speakers.
  Snapping to sentence ends fixed 8 of the wrong boundaries in an 11-minute call.
