# The background voiceprint

`src/Sakshi.Speakers/background.json` is the mean voiceprint Sakshi starts from before it has seen any meetings
(see `docs/design/speakers.md`, "Background mean"). It belongs to one embedding model: if the model changes, make it
again.

How it was made:

1. Download LibriSpeech dev-clean (CC BY 4.0) from <https://www.openslr.org/12> and unpack it.
2. For each of its 40 speakers, join that speaker's first four `.flac` files (sorted by path) and keep the first
   40 seconds as 16 kHz mono 16-bit WAV:

   ```
   ffmpeg -i a.flac -i b.flac -i c.flac -i d.flac -filter_complex "concat=n=4:v=0:a=1" \
          -t 40 -ac 1 -ar 16000 -c:a pcm_s16le SPEAKER.wav
   ```

3. Compute each WAV's voiceprint with sherpa-onnx's `SpeakerEmbeddingExtractor` and
   `wespeaker_en_voxceleb_resnet34_LM.onnx` (the whole file as one stream).
4. Average the 40 voiceprints element by element, round to 6 decimals, and write `mean` with `count: 40`, the model's
   name and its SHA-256.
