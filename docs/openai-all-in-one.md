# OpenAI All In One snapshot translation

OpenAI All In One is an experimental, visual-only Snapshot Processing mode on
the **OCR & Detection** settings page. It sends one captured image to OpenAI,
asks the image model to translate visible text in place, and displays the
generated image over the capture region.

This mode is deliberately separate from the OCR provider setting:

- **Snap** uses the selected Snapshot Processing method.
- **Auto** and other realtime processing always use the normal OCR and
  translation pipeline shown below it in Settings.

Image generation is too slow and potentially too costly for continuous capture.
It can also alter image details despite the preservation instructions, so the
result should be treated as a convenient visual translation rather than a
pixel-perfect or authoritative document translation.

## Setup and behavior

1. Open Settings, then **OCR & Detection**.
2. Under **Snapshot Processing**, select **OpenAI All In One (Experimental)**.
3. Enter an OpenAI API key with access to `gpt-image-2` and use **Test API
   Access**.
4. Choose Low, Medium, or High quality. Medium is the default.
5. Choose an **Uploaded input size** and **Generated output budget**. The
   defaults are a 1024-pixel input maximum edge and OpenAI's minimum legal
   output size (655,360 pixels).
6. Close Settings and press **Snap**.

The capture is uploaded to OpenAI. A running timer reports image preparation
and translation/rendering. After completion, the elapsed time remains visible
in seconds and the floating toolbar adds a **Show Original / Show Translated**
button for a direct comparison. Press Snap again to cancel and clear the
snapshot. Starting another Snap clears the previous generated image before
capture so it cannot accidentally be captured again.

The result toolbar and Settings page report the exact dimensions and pixel
counts for the downscaled upload, requested generated output, image actually
returned by OpenAI, and final overlay restored to the capture dimensions. This
makes any server-side difference from the requested output visible.

The Main and Monitor overlay selectors work as follows while an All In One
snapshot is active:

| Window | Translated | Source or Hide |
| --- | --- | --- |
| Main | Generated translated image | Original captured image, or no overlay for Hide |
| Monitor | Generated translated image | Original captured image |

The comparison button switches both Main and Monitor between the preserved
original capture and generated translation. The normal overlay radio buttons
remain available for independent display control.

**Save Screenshot** also uses these preserved images. Screenshot type Source
saves the original capture, Target saves the generated image, and Both saves
one of each; it does not recapture the desktop or rasterize the overlay window.

The v1 feature does not create OCR text objects. It therefore does not populate
ChatBox text, TTS, editable text blocks, translation history, or structured-text
exports.

## API and image handling

The implementation uses the single-shot Image Edits endpoint at
`POST /v1/images/edits` with `gpt-image-2`, PNG input/output, one image, and the
selected quality. It does not send `input_fidelity`, because `gpt-image-2`
automatically uses high input fidelity. See OpenAI's
[image editing guide](https://developers.openai.com/api/docs/guides/image-generation#edit-images)
and [image output constraints](https://developers.openai.com/api/docs/guides/image-generation#customize-image-output).

Upload and generated-output sizing are independent. The upload can be reduced
to a 256-pixel maximum edge to reduce transfer size and model input detail. The
generated output is selected by a target pixel budget; OpenAI currently requires
at least 655,360 output pixels, so UGTLive cannot request a smaller generated
image. Both dimensions are normalized to multiples of 16, at most 3840 pixels
per edge, and an aspect ratio no wider or taller than 3:1. Extreme panoramas are
symmetrically padded. The generated result is then cropped to the tracked
content rectangle and restored to the exact source dimensions.

The `gpt-image-2` Image Edits API does not expose a thinking or reasoning-effort
parameter. UGTLive therefore does not show a nonfunctional thinking control;
quality, upload dimensions, and generated-output dimensions are the available
speed, cost, and fidelity controls.

Requests have a five-minute timeout. A new Snap, switching Snapshot Processing
method, clearing the current snapshot, or application shutdown cancels the
active request and invalidates late results.

## Privacy, credentials, and cost

- The full captured image is sent to OpenAI. Do not use this mode for captures
  containing information that should not leave the computer.
- The dedicated key is stored using the same local application configuration
  mechanism as other provider keys. It must never be printed by logs or test
  harness output.
- Each Snap is a billable image-edit request. Quality and image dimensions can
  affect latency and cost; consult current OpenAI pricing before sustained use.
- Authentication, model access or organization verification, quota/rate-limit,
  timeout, malformed-response, and network errors are reduced to actionable UI
  messages. Authorization headers and API keys are not logged.

## Test harness

Run the offline contract suite after changing this feature:

```powershell
dotnet .\app\ugtlive_debug.dll --test-openai-all-in-one --contract-only
```

It checks dimension rules, prompt language substitution, multipart and
authorization construction, successful base64 decode/restoration, API error
mapping, and request cancellation without making a billable network call.

For an opt-in live smoke test:

```powershell
dotnet .\app\ugtlive_debug.dll --test-openai-all-in-one `
  --image .\sample.png --source ja --target en --output .\translated.png `
  --input-max-edge 512 --output-target-pixels 655360
```

The live harness reads `OPENAI_API_KEY` first and otherwise reads the dedicated
saved setting. The two sizing arguments are optional and otherwise use the saved
settings. It never accepts or prints a key on the command line. The live test
makes a real API request and may incur charges. It prints the exact uploaded,
requested, returned, and restored dimensions and pixel counts.

## Manual verification checklist

- Missing and invalid API keys, denied model access, and quota/rate-limit errors.
- Low, Medium, and High quality requests.
- All upload maximum-edge and generated-output budget choices, including the
  256-pixel input and 655,360-pixel output minimums.
- Exact uploaded/requested/returned/restored pixel telemetry in Settings and the
  result toolbar.
- Timer stage changes, user cancellation, and stale-result suppression.
- Snap-toggle clearing and recapture prevention.
- Independent Main and Monitor Source/Translated/Hide modes.
- Capture-window resize behavior and extreme portrait/landscape captures.
- Standard Snap, Auto, OCR text overlays, and normal translation regressions.
