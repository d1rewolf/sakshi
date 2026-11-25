# Third-party notices

## FFmpeg (shipped in the installers)

- The installers put an `ffmpeg` executable (`ffmpeg.exe` on Windows) next to `sakshi`. Sakshi runs it as a separate
  program to read recordings; it is not linked into Sakshi.
- FFmpeg 9.1, built from the official source by `packaging/build-ffmpeg.sh`: LGPL build, no external libraries.
- Licence: GNU Lesser General Public License version 2.1 or later. The licence text ships next to the executable as
  `ffmpeg-COPYING.LGPLv2.1.txt`.
- Source: the exact tarball it was built from, `ffmpeg-9.1.tar.xz`, is attached to every Sakshi release; it is also at
  https://ffmpeg.org/releases/ffmpeg-9.1.tar.xz. The build script shows how it was configured.
- FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.

## sherpa-onnx (speaker labels)

- Used by `src/Sakshi.Speakers` to find who is speaking and to compare voices. It runs inside Sakshi, from the NuGet
  package `org.k2fsa.sherpa.onnx`; its native libraries ship in the installers.
- Version: 2.0.3.
- Licence: Apache License 2.0. https://www.apache.org/licenses/LICENSE-2.0
- Source: https://github.com/k2-fsa/sherpa-onnx

## ONNX Runtime (inside sherpa-onnx)

- The sherpa-onnx package includes ONNX Runtime, which runs the two speaker models below.
- Licence: MIT.
- Source: https://github.com/microsoft/onnxruntime

## pyannote segmentation 3.0 (speaker model, downloaded on first use)

- Finds where each person speaks. Not shipped: Sakshi downloads `pyannote-segmentation-3-0.onnx` the first time a
  transcript needs speaker labels, into the `speakers` folder next to the speech model.
- Model: pyannote/segmentation-3.0. https://huggingface.co/pyannote/segmentation-3.0
- Licence: MIT. Copyright (c) 2022 CNRS.
- ONNX export used: https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0

## WeSpeaker ResNet34-LM (speaker model, downloaded on first use)

- Turns a stretch of speech into a voiceprint. Not shipped: Sakshi downloads `wespeaker_en_voxceleb_resnet34_LM.onnx`
  the first time a transcript needs speaker labels, into the `speakers` folder next to the speech model.
- Model: WeSpeaker ResNet34-LM, trained on VoxCeleb. https://github.com/wenet-e2e/wespeaker
- Training data: VoxCeleb. https://www.robots.ox.ac.uk/~vgg/data/voxceleb/
- Licence: Creative Commons Attribution 4.0 (CC BY 4.0). https://creativecommons.org/licenses/by/4.0/
- ONNX file: from the sherpa-onnx releases, https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models

## LibriSpeech (background for voice matching)

- Only a mean voiceprint derived from LibriSpeech speech ships, in `src/Sakshi.Speakers/background.json`; no audio is
  included. It is used to make voices easier to tell apart. How it was made: `tools/speakers/README.md`.
- Corpus: LibriSpeech, https://www.openslr.org/12. Panayotov, Chen, Povey and Khudanpur, "LibriSpeech: an ASR corpus
  based on public domain audio books", ICASSP 2015.
- Licence: Creative Commons Attribution 4.0 (CC BY 4.0). https://creativecommons.org/licenses/by/4.0/

## Symbols Nerd Font Mono (bundled font)

- File: `src/Sakshi.App/Assets/SymbolsNerdFontMono-Regular.ttf`, embedded in the app so the prompt glyphs show on macOS and Windows.
- Name: Symbols Nerd Font Mono, Regular.
- Version: Nerd Fonts v3.5.1, from the release asset `NerdFontsSymbolsOnly.zip`.
- Source: https://github.com/ryanoasis/nerd-fonts/releases/tag/v3.5.1
- Font copyright (from its name table): Copyright (c) 2016, Ryan McIntyre.
- Nerd Fonts project licence (covers the patcher and project files, not the icon sets below): MIT, copy in `src/Sakshi.App/Assets/LICENSE`; upstream https://github.com/ryanoasis/nerd-fonts/blob/v3.5.1/LICENSE.

The font is a collection of icon sets, each under its own licence. The table is copied from the `README.md` shipped inside `NerdFontsSymbolsOnly.zip` (v3.5.1), the project's own list. For the full licence text of each set, see its upstream repository.

| Icon set | Upstream | Version | Licence (as listed by Nerd Fonts) |
|---|---|---|---|
| Codicons | https://github.com/microsoft/vscode-codicons | 0.0.45 | CC BY 4.0 |
| Devicons | https://github.com/devicons/devicon | 2.17.0 | MIT |
| extraglyphs | https://github.com/source-foundry/Hack | - | MIT |
| Font Awesome | https://github.com/FortAwesome/Font-Awesome | 6.5.1 | CC BY 4.0 |
| Font Awesome Extension | https://github.com/AndreLZGava/font-awesome-extension | 0.0.3 | MIT |
| Font Logos | https://github.com/lukas-w/font-logos | 1.3.0 | listed as "unlicensed"; see the upstream repository |
| MaterialDesign | https://github.com/Templarian/MaterialDesign-Font | Oct 6, 2022 | Apache 2.0 |
| Octicons | https://github.com/primer/octicons | 18.3.0 | MIT |
| Seti and original | https://github.com/jesseweed/seti-ui | 0.8.1 | MIT |
| Pomicons | https://github.com/gabrielelana/pomicons | 1.001 | OFL 1.1 RFN |
| Powerline Extra | https://github.com/ryanoasis/powerline-extra-symbols | 1.200 | MIT |
| Powerline Symbols | https://github.com/powerline/powerline | 1.000 (ca 2013) | MIT |
| Power Symbols IEC | https://github.com/jloughry/Unicode | Feb 2015 | MIT |
| Weather Icons | https://github.com/erikflowers/weather-icons | 2.0.10 (1.100) | OFL 1.1 |

Attribution for the CC BY 4.0 sets (Codicons, Font Awesome): see https://creativecommons.org/licenses/by/4.0/ and the upstream repositories above. Apache 2.0 (MaterialDesign): https://www.apache.org/licenses/LICENSE-2.0. SIL OFL 1.1: https://openfontlicense.org/.
