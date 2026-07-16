# Settings connection tests

The Settings window provides end-to-end tests anywhere a remote API,
subscription CLI, local translation model, or TTS voice is configured:

- OCR: Google Vision sends a generated image and displays the actual API result.
- Translation: every provider sends a small Japanese-to-English request through
  its normal `ITranslationService` implementation. This includes Gemini,
  ChatGPT, Anthropic, OpenRouter, Google Translate, Ollama, llama.cpp, Anthropic
  Sub, OpenAI Sub, and Gemini CLI (Enterprise).
- TTS: ElevenLabs, Google Cloud TTS, and Qwen3-TTS generate a short sample with
  the selected voice and play the returned audio. This is available both on the
  main Audio tab and in the source/target TTS voice selector dialogs.
- Listen: OpenAI Realtime opens the same WebSocket endpoint used by the selected
  audio translation mode and displays the initial server reply.

Successes and errors are displayed inline in Settings. The result fields are
read-only but selectable, so the full reply can be copied with Ctrl+C or the
standard context-menu Copy command. Translation services that already show a
detailed error dialog also copy that detail into the inline result.

## Command-line automation

The executable exposes the same shared implementation without opening any UI:

```powershell
.\ugtlive.exe --test-settings-connection translation
.\ugtlive.exe --test-settings-connection translation --provider OpenRouter --model google/gemini-2.5-flash
.\ugtlive.exe --test-settings-connection translation --provider "OpenAI Sub" --model gpt-5.4
.\ugtlive.exe --test-settings-connection tts --service ElevenLabs --voice 21m00Tcm4TlvDq8ikWAM
.\ugtlive.exe --test-settings-connection tts --service Qwen3-TTS --voice ono_anna --play-audio
.\ugtlive.exe --test-settings-connection realtime
.\ugtlive.exe --test-settings-connection vision
```

The harness reads API keys and subscription authentication from the saved app
configuration; do not put secrets on the command line. Provider/model/service
overrides are restored before the process exits. TTS playback is disabled in
headless mode unless `--play-audio` is supplied.

Exit codes are `0` for a successful live response, `1` for a failed test, and
`2` for invalid command usage. The latest output is also written beside the
executable as `settings_connection_test_result.txt`, since the application uses
the Windows GUI subsystem and redirected console output is not always available.

These tests make real provider calls and may consume API quota, subscription
allowance, or local compute.

## Google subscription CLI status

As of June 18, 2026, Google no longer serves personal, free, Google AI Pro, or
Google AI Ultra requests through Gemini CLI. Those tiers moved to Antigravity
CLI. UGTLive therefore labels the existing `GeminiCli` backend as **Gemini CLI
(Enterprise)**; it remains available for Gemini Code Assist Standard/Enterprise
and supported Google Cloud accounts. The internal ID remains `GeminiCli` so
existing configurations continue to load.

Antigravity CLI is not currently a safe replacement for this backend. Although
its official `agy -p` print mode performs a one-shot request, current Windows
versions can complete the model call and exit successfully while emitting no
capturable stdout to a parent process. Until Google provides reliable headless
response output, individual users should select the regular **Gemini** provider
and use a Gemini API key instead. Recheck the upstream Antigravity CLI stdout
issues before adding an `agy` backend.
