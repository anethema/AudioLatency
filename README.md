# AudioLatency

A Windows desktop utility for measuring the audible delay of Bluetooth speakers, headphones, and other playback devices using a built-in or USB microphone.

The app sends a short chirp, finds it in the microphone recording, and reports the time between submitting its audio buffer and the microphone's timestamped sample. Repeated trials show the median, variation, and detection quality. A wired-reference comparison estimates the additional delay of another output path.

## Run

On Windows 10 or 11, run `AudioLatency.exe` from the portable `AudioLatency-win-x64` build. The portable build includes its .NET runtime and requires no Python installation.

From this checkout:

```powershell
.\build.ps1 -Publish
.\run.ps1
```

Building requires the **.NET 8 SDK**, including Windows desktop build support. The scripts also recognize an optional SDK at `.tools/dotnet/`; that directory is local-only and excluded from source control. An installed runtime alone cannot build the app.

## Measure a device

1. Pair the Bluetooth speaker or headphones in Windows, or connect the output to test.
2. Select that playback device and a **built-in or USB microphone** in the app. Avoid using the Bluetooth headset's own microphone: that may change the headset's audio mode and adds a second wireless path.
3. Place the microphone close to the speaker or headphone earcup. Keep its position fixed across trials. Start with a conservative volume.
4. Choose the number of trials, and optionally enter the speaker-to-microphone distance in centimeters. A distance of zero leaves acoustic travel time in the result.
5. Start the test. The microphone meter operates during recording. Wait for recording and analysis to finish; inspect the individual trials as well as the median.

The test records only while it is running. Recordings are held in memory and are not saved or uploaded. Exported JSON/CSV files contain measurements and device information, not microphone audio. Nothing plays or records automatically at launch.

If all trials fail, check Windows microphone privacy settings, microphone levels and the selected output. Move the mic closer, reduce volume if clipping is reported, and disable microphone noise suppression, echo cancellation or other enhancements where possible. Those effects can suppress the chirp or add variable delay. Leave microphone settings unchanged between comparison runs.

## What the numbers mean

**App-to-microphone latency:** the elapsed time from submission of the first chirp frame to WASAPI to the detected microphone sample, optionally minus acoustic travel time. It includes the app's queued output, remaining Windows rendering, output hardware/Bluetooth processing, and input delays not compensated by the microphone driver. It excludes the time spent delivering the microphone packet to application code. This is a useful end-to-end acoustic-path estimate, not an independently calibrated laboratory measurement.

**App queue:** the output padding observed when the chirp buffer is prepared, converted to milliseconds. This is an estimate of the audio already queued in this application's endpoint buffer. It is not all Windows buffering and is not the Bluetooth latency. The app requests a 40 ms shared-mode output buffer; the actual size depends on the endpoint and is included in the session export.

**Jitter:** population standard deviation of accepted trial delays. The main result is the median. Rejected trials are excluded from both statistics; inspect the accepted count before relying on a run.

**Estimated extra output delay:** the median of `(app-to-mic latency − app queue)` for the current run, minus that same median for a wired reference. At least three accepted trials in each run, the same microphone and microphone sample rate, and different output devices are required. Negative values mean the current path measured faster than the reference.

To create a reference, measure a wired speaker/headphone output, save that run as the reference, then test the Bluetooth output. You can save/load the reference as a local JSON file. Keep microphone processing, levels, geometry, and other settings as similar as possible; distance compensation can account for changed acoustic spacing. The app cannot verify that a selected reference is physically wired.

This comparison approximately cancels shared microphone/system delays. Different output drivers and hardware also have different delays, so the difference **does not isolate the Bluetooth radio, codec, or Windows stack individually**. A single microphone cannot identify those components separately. Advertised driver latency is shown in session metadata and is deliberately not subtracted as if it were a measured calibration.

## Measurement method and limits

- A 30 ms windowed frequency sweep is rendered through WASAPI shared mode. Its frequency span fits both devices' sample rates.
- Playback runs on a dedicated thread. A high-resolution performance-counter timestamp brackets submission of the first chirp frame. Interrupted submissions are rejected.
- Microphone packets include WASAPI sample-position and performance-counter timestamps. Detection uses the timestamp of the matching packet plus its sample offset, rather than callback arrival time.
- Normalized FFT cross-correlation detects the strongest matching sweep, including inverted polarity. Independent playback/input sample rates are supported.
- Timestamp errors, gaps, output underruns, clipping, weak matches, and competing matches are rejected. Correlation is a match score, not a calibrated probability or accuracy guarantee.
- Detection searches up to **1,500 ms** after submission. Trials are approximately two seconds apart. Very late echoes or delays longer than that window are unsupported.
- Acoustic compensation assumes 343 m/s (`distance_cm / 34.3` milliseconds). Air temperature and placement introduce uncertainty.
- Driver timestamps may be inaccurate or apply internal compensation. Displaying tenths of a millisecond does not guarantee that absolute accuracy. Validate with a known wired setup before comparing devices.
- Intermittent chirps can include Bluetooth/device wake-up behavior. This version does not separately measure continuous-playback and cold-start latency.

## Development

```powershell
dotnet build AudioLatency.sln -c Release
dotnet run --project tests/AudioLatency.Core.Tests -c Release
dotnet run --project src/AudioLatency.App
```

The regression harness has no external test-framework dependency and exits nonzero on failure. It checks signal detection under noise, polarity and sample-rate changes, rejection conditions, statistics and wired-reference comparison. Automated tests do not replace a real acoustic test.

- `src/AudioLatency.Core/`: chirp generation, matched filtering, statistics and session models.
- `src/AudioLatency.App/`: WPF interface, exports and WASAPI measurement service.
- `tests/AudioLatency.Core.Tests/`: deterministic signal-processing regression checks.
- `.github/workflows/build.yml`: Windows build, regression checks and portable app artifact.
- `STATE.md`: current project state, verification and next-session notes.

## Audio API references

- [WASAPI capture sample positions and timestamps](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)
- [Windows render and capture latency terminology](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)
- [NAudio](https://github.com/naudio/NAudio) (MIT-licensed; this project currently uses NAudio.Wasapi 2.2.1)

## Contributions

Use a feature branch and a pull request for changes. Run `build.ps1` before opening the request and describe any hardware testing separately from automated checks. No project license has been selected yet; public availability alone does not grant an open-source license.
