# AudioLatency

A Windows desktop utility for measuring the audible delay of Bluetooth speakers, headphones, and other playback devices using a built-in or USB microphone.

The app sends a short chirp, finds it in the microphone recording, and measures the time between submitting its audio buffer and the microphone's timestamped sample. The main result estimates the remaining latency after removing this test's measured output queue; the original app-to-microphone time and queue remain visible. Repeated trials show the median, variation, and detection quality. A wired-reference comparison estimates the additional delay of another output path.

## Download and run the portable app

The portable app runs on **Windows 10 or 11 (x64)**. It includes its .NET runtime, so you do not need to install .NET, Python, or development tools to use it. No installer is required.

**Download it from GitHub Actions:**

1. **Sign in to GitHub.** GitHub requires you to be signed in to download Actions artifacts, even for a public repository.
2. Open [anethema/AudioLatency](https://github.com/anethema/AudioLatency) and click the **Actions** tab near the top of the page.
3. In the left sidebar, click **Windows build**. You can also [open the Windows build workflow directly](https://github.com/anethema/AudioLatency/actions/workflows/build.yml).
4. Open the most recent **successful run for the `main` branch**, marked with a green check. The run's title may be a commit message; the branch name is shown alongside it. Builds from other branches may contain changes that have not been merged yet.
5. On the run's **Summary** page, scroll down to **Artifacts** and click **AudioLatency-win-x64** to download the ZIP file.
6. In Windows File Explorer, right-click the downloaded ZIP and choose **Extract All…**. Open the extracted folder and double-click **AudioLatency.exe**.

The ZIP may also contain `.pdb` files. These are debugging symbols and are optional for portable use; you can copy just `AudioLatency.exe` to another folder or Windows x64 PC.

**The repository's Code → Download ZIP button downloads source code, not the portable app.** The executable is supplied separately as the Actions artifact above. There is no compiled `artifacts/` folder in the repository: that folder is generated locally when building from source and is excluded from Git.

If you cannot find the download, make sure you are signed in, opened a completed successful **Windows build** run, and are viewing its **Summary** rather than an individual job's logs. Older artifacts can expire; use a recent successful run. See [GitHub's artifact download instructions](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts) for additional help.

## Measure a device

1. Pair the Bluetooth speaker or headphones in Windows, or connect the output to test.
2. Select that playback device and a **built-in or USB microphone** in the app. Avoid using the Bluetooth headset's own microphone: that may change the headset's audio mode and adds a second wireless path.
3. Place the microphone close to the speaker or headphone earcup. Keep its position fixed across trials. Start with a conservative volume.
4. Choose the number of trials, and optionally enter the speaker-to-microphone distance in centimeters. A distance of zero leaves acoustic travel time in the result.
5. Start the test. The microphone meter operates during recording. Wait for recording and analysis to finish; inspect the queue-adjusted median, the raw app-to-mic time and app queue beneath it, and the individual trials.

The test records only while it is running. Recordings are held in memory and are not saved or uploaded. Exported JSON/CSV files contain measurements and device information, not microphone audio. Nothing plays or records automatically at launch.

Both device selectors open when you click anywhere in the field, including the selected device name. When updating the app, save any reference you want to keep before closing an older instance; an already running process continues using its original version.

If all trials fail, check Windows microphone privacy settings, microphone levels and the selected output. Move the mic closer, reduce volume if clipping is reported, and disable microphone noise suppression, echo cancellation or other enhancements where possible. Those effects can suppress the chirp or add variable delay. Leave microphone settings unchanged between comparison runs.

## What the numbers mean

**Queue-adjusted latency (the large result):** the median of `(app-to-mic latency − app queue)` calculated separately for each accepted trial. For example, a 60 ms app-to-mic measurement with a 20 ms queue gives an approximately 40 ms adjusted estimate. Removing the measured queue makes it easier to compare the remaining audio path without including this test's own queued output. It still includes downstream Windows buffering, output hardware/Bluetooth processing, acoustic travel unless compensated, and input delays not compensated by the microphone driver. It is not a calibrated device-only measurement.

**Raw app-to-mic latency:** the original elapsed time from submission of the first chirp frame to WASAPI to the detected microphone sample, optionally minus acoustic travel time. This includes the app queue and is shown below the large result and in the trial table. Packet-delivery scheduling is excluded. The raw and adjusted values both retain the configured acoustic distance correction.

**App queue:** the output padding observed when the chirp buffer is prepared, converted to milliseconds. This is an estimate of the audio already queued in this application's endpoint buffer. It is not all Windows buffering and is not the Bluetooth latency. The app requests a 40 ms shared-mode output buffer; the actual size depends on the endpoint and is included in the session export.

Other sound applications also buffer audio, but their queues depend on their audio API, buffer settings, scheduling and device. A 20 ms queue in this test is not a fixed overhead shared by all apps. The adjusted estimate does not predict the exact latency of a game, music player or recording application.

**Jitter:** the displayed population standard deviation of accepted **queue-adjusted** trial delays, matching the main result and graph. Rejected trials are excluded from these statistics; inspect the accepted count before relying on a run. Each trial is adjusted before aggregation; subtracting the two separately calculated medians can give a different result. A negative adjusted value is retained and flagged as queue/timestamp uncertainty rather than being silently rounded up to zero; it does not represent negative physical latency.

**Estimated extra output delay:** the median of `(app-to-mic latency − app queue)` for the current run, minus that same median for a wired reference. At least three accepted trials in each run, the same microphone and microphone sample rate, and different output devices are required. Negative values mean the current path measured faster than the reference.

To create a reference, measure a wired speaker/headphone output, save that run as the reference, then test the Bluetooth output. You can save/load the reference as a local JSON file. Keep microphone processing, levels, geometry, and other settings as similar as possible; distance compensation can account for changed acoustic spacing. The app cannot verify that a selected reference is physically wired.

This comparison approximately cancels shared microphone/system delays. Different output drivers and hardware also have different delays, so the difference **does not isolate the Bluetooth radio, codec, or Windows stack individually**. A single microphone cannot identify those components separately. Advertised driver latency is shown in session metadata and is deliberately not subtracted as if it were a measured calibration.

Saved references from earlier versions remain compatible. JSON fields `LatencyMs`, `MedianMs` and `JitterMs` keep their original raw meanings; `QueueAdjustedLatencyMs`, `MedianQueueAdjustedMs` and `QueueAdjustedJitterMs` provide the adjusted values separately. CSV retains its existing columns and adds `queue_adjusted_latency_ms` at the end. Loading an older reference derives the adjusted values from its stored raw time and queue, without subtracting the queue twice.

## Measurement method and limits

- A 30 ms windowed frequency sweep is rendered through WASAPI shared mode. Its frequency span fits both devices' sample rates.
- Playback runs on a dedicated thread. A high-resolution performance-counter timestamp brackets submission of the first chirp frame. Interrupted submissions are rejected.
- Microphone packets include WASAPI sample-position and performance-counter timestamps. Detection uses the timestamp of the matching packet plus its sample offset, rather than callback arrival time.
- Normalized FFT cross-correlation detects the strongest matching sweep, including inverted polarity. Independent playback/input sample rates are supported.
- Detection matches the sweep's waveform, rather than triggering on a volume threshold alone. Room sounds that do not resemble it generally produce weaker matches, but loud interference can mask it and strong reflections can shift the detected peak. The match score is not a guarantee that every accepted result is correct; the current noise tests use synthetic Gaussian noise, not a broad set of real room recordings.
- Timestamp errors, gaps, output underruns, clipping, weak matches, and competing matches are rejected. Correlation is a match score, not a calibrated probability or accuracy guarantee.
- Detection searches up to **1,500 ms** after submission. Trials are approximately two seconds apart. Very late echoes or delays longer than that window are unsupported.
- Acoustic compensation assumes 343 m/s (`distance_cm / 34.3` milliseconds). Air temperature and placement introduce uncertainty.
- Driver timestamps may be inaccurate or apply internal compensation. Displaying tenths of a millisecond does not guarantee that absolute accuracy. Validate with a known wired setup before comparing devices.
- Intermittent chirps can include Bluetooth/device wake-up behavior. This version does not separately measure continuous-playback and cold-start latency.

## Development

### Build the portable app from source

Clone the repository or download and extract its source ZIP, then open PowerShell in the project folder. Building requires the **.NET 8 SDK**, including Windows desktop build support. An installed runtime alone cannot build the app. The scripts also recognize an optional local SDK at `.tools/dotnet/`; that directory is excluded from Git.

```powershell
.\build.ps1 -Publish
.\run.ps1
```

The build script compiles the app, runs the regression checks, and publishes the portable executable to **`artifacts/AudioLatency-win-x64/AudioLatency.exe` on your own computer**. The `artifacts/` folder is created by the build; it is not a folder to look for in GitHub's repository file list. `run.ps1` launches the most recently published local portable build, including builds in versioned sibling folders.

### Build and test during development

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
- [WASAPI queued frames and endpoint padding](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getcurrentpadding)
- [Windows render and capture latency terminology](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)
- [NAudio](https://github.com/naudio/NAudio) (MIT-licensed; this project currently uses NAudio.Wasapi 2.2.1)

## Contributions

Use a feature branch and a pull request for changes. Run `build.ps1` before opening the request and describe any hardware testing separately from automated checks. No project license has been selected yet; public availability alone does not grant an open-source license.
