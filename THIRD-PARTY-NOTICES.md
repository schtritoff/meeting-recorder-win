# Third-party notices

Meeting Recorder is distributed under the MIT license in `LICENSE`.

## Native speech engine

The bundled Windows x64 libraries use transcribe.cpp 0.4.1 and ggml, with miniz and Silero VAD components. Their license texts and copyright notices are included under `native/win-x64/licenses/` in source and `licenses/native/` in the downloadable ZIP:

- transcribe.cpp: MIT, `LICENSE`
- ggml: MIT, `ggml/LICENSE`
- miniz: MIT, `src/third_party/miniz/LICENSE`
- Silero VAD: MIT, `src/third_party/silero_vad/LICENSE`

## Managed audio libraries

[NAudio 2.2.1](https://github.com/naudio/NAudio), including its Core, WASAPI, WinMM, MIDI, ASIO and WinForms packages, is MIT licensed. The full license is in `licenses/NAudio-LICENSE.txt`.

## .NET runtime

The self-contained Windows package includes the Microsoft .NET runtime and Windows Desktop runtime. Their license and third-party notices are included under `licenses/dotnet/` in the release ZIP, copied from the runtime packs used to build it.

## Speech model (downloaded separately)

The app downloads the [GGUF quantization](https://huggingface.co/infomiho/diktafon-dictate-hr-1-GGUF) of [infomiho/diktafon-dictate-hr-1](https://huggingface.co/infomiho/diktafon-dictate-hr-1), based on NVIDIA Canary 1B v2. The model is licensed under [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/). Model author: infomiho. The linked GGUF version is quantized; the app does not modify the downloaded model. The model is not included in the release ZIP.
