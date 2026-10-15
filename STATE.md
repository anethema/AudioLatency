# Project state

Updated: 2026-10-15

## Goal

Build a Windows GUI that measures speaker/headphone acoustic latency with a system microphone, including Bluetooth devices. Support useful total-path estimates and a wired-reference comparison without claiming to isolate pure Bluetooth latency.

## Current implementation

- C# / .NET 8 WPF desktop app with NAudio.Wasapi 2.2.1.
- Core chirp generation, normalized FFT cross-correlation, match rejection, statistics and reference comparison.
- WASAPI shared-mode playback and capture on a dedicated thread, submission QPC and microphone packet timestamps.
- Device selection, repeated trials, microphone meter during tests, measurement table/graph, wired-reference management and JSON/CSV exports.
- Root build/run scripts, Windows CI and portable x64 publish configuration.

## Measurement decisions

- Main metric: buffer submission to microphone sample, with optional distance correction. Capture packet-delivery scheduling is excluded.
- App queue is observed from output padding. Wired comparison subtracts that estimate per trial before comparing medians.
- Driver-reported input latency is metadata, not a reliable independent calibration; do not subtract it again.
- Reject missing samples, unreliable timestamps, output underruns, clipping and ambiguous/weak detections.
- Detection window 1.5 s, 30 ms chirp, roughly 2 s between trials; input channel 1.
- All microphone audio stays in memory. JSON/CSV exports contain results only.

## Build and verification

- Optional local .NET SDK 8.0.426 installed at `.tools/dotnet/`; not committed.
- Release solution build passed with zero warnings/errors; regression harness passed 10/10 checks.
- Portable self-contained x64 publish succeeded at `artifacts/AudioLatency-win-x64/AudioLatency.exe`.
- GUI launched and was visually inspected. Device enumeration found seven outputs and four inputs on the development PC.
- Two real WASAPI start/cancel/release cycles and two GUI start/cancel/restart cycles passed before the first chirp. Cancel button re-enable and device selector contrast were fixed during review.
- Synthetic tests cover noise, polarity, differing sample rates, clipping, ambiguous matches, search boundaries, statistics and per-trial queue removal in reference comparisons.
- Physical acoustic timing, wired/Bluetooth comparisons and mid-run device-disconnect behavior remain unverified.

## GitHub

- Public repository created: `https://github.com/anethema/AudioLatency`.
- GitHub plugin authenticates as `anethema` and confirms repository push access.
- Native Git Credential Manager sign-in was available for repository setup; no new login was needed.
- Local Git initialized with `origin` pointing to the public repository.
- Main contains the repository baseline; implementation is on `feature/acoustic-latency-app`.
- Draft implementation PR: `https://github.com/anethema/AudioLatency/pull/1` (left unmerged pending hardware testing).
- Native Git pushes and pull-request creation succeeded. The plugin's PR creation call returned an integration-permission error; native GitHub authentication provided the working path.
- Windows CI passed build, regression checks, portable publish and artifact upload: `https://github.com/anethema/AudioLatency/actions/runs/38807708112`.

## Next steps

1. Test a known wired output and a Bluetooth output with the same microphone. Measure real-world accuracy and rejection rate.
2. Exercise disconnect, unavailable microphone and all-trials-rejected cases in the GUI. Cancel/restart has already passed two automated GUI smoke cycles.
3. Review the measurements, then mark PR #1 ready and merge when accepted.
4. Consider continuous-playback warm-up to separate wake-up behavior from steady latency.
5. Choose a project license before inviting reuse.

## Known limits

Absolute accuracy depends on device/driver timestamps and microphone processing. A single microphone cannot independently determine Windows, capture hardware and Bluetooth delays. Wired comparison approximates additional output delay. Current tests cover synthetic signals rather than a calibrated physical loopback. Cold-start versus steady continuous playback is not distinguished yet.
