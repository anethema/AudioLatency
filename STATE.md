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
- Version 0.1.1 fixes both device selectors so the entire field opens the dropdown. The full-width toggle preserves the existing selection and keyboard behavior.

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
- Owner-reported hardware smoke test: approximately 30 ms for a wired setup and 362 ms for a Bluetooth setup. This supports basic physical operation; the roughly 332 ms difference is additional output-path delay, not a calibrated Bluetooth-only measurement. Microphone/geometry equivalence and absolute accuracy have not been independently verified.
- Version 0.1.1 Release build and all 10 regression checks passed. GUI checks verified the text side, center and arrow side of both selectors are full-field dropdown targets.
- The updated portable build is at `artifacts/AudioLatency-win-x64-0.1.1/AudioLatency.exe`. The existing v0.1.0 process was left open to preserve any unsaved measurements or reference. `run.ps1` now chooses the most recently published portable build.
- Mid-run device-disconnect behavior and calibrated absolute timing remain unverified.

## GitHub

- Public repository created: `https://github.com/anethema/AudioLatency`.
- GitHub plugin authenticates as `anethema` and confirms repository push access.
- Native Git Credential Manager sign-in was available for repository setup; no new login was needed.
- Local Git initialized with `origin` pointing to the public repository.
- PR #1 was merged: `https://github.com/anethema/AudioLatency/pull/1`. Main includes the app and v0.1.1 full-field device-selector fix.
- Further changes use a fresh branch and separate pull request. The README download guide is on `feature/document-downloads`.
- Native Git pushes and pull-request creation succeeded. The plugin's PR creation call returned an integration-permission error; native GitHub authentication provided the working path.
- Windows CI passed build, regression checks, portable publish and artifact upload: `https://github.com/anethema/AudioLatency/actions/runs/38807708112`.
- Portable builds are downloaded from successful Windows build workflow runs as `AudioLatency-win-x64` ZIP artifacts. GitHub sign-in is required. The source ZIP and repository tree do not include the generated local `artifacts/` folder.
- README now gives repository-page-to-download steps, extraction/run instructions, optional PDB guidance, artifact troubleshooting, and a separate source-build section with the local output path. Directions were checked against the workflow and official GitHub artifact-download documentation; application code is unchanged.

## Next steps

1. Extend the initial wired/Bluetooth smoke test with repeatable geometry and settings; measure real-world accuracy and rejection rate.
2. Exercise disconnect, unavailable microphone and all-trials-rejected cases in the GUI. Cancel/restart has already passed two automated GUI smoke cycles.
3. Review and merge the separate README download-guide pull request when accepted.
4. Consider continuous-playback warm-up to separate wake-up behavior from steady latency.
5. Choose a project license before inviting reuse.
6. Evaluate optional trial-specific coded probes and a peak-versus-background match criterion against speech, music, impulsive sounds and reverberation. The current signal is still a 30 ms chirp; no coded-signal mode or new latency decomposition has been added.

## Known limits

Absolute accuracy depends on device/driver timestamps and microphone processing. A single microphone cannot independently determine Windows, capture hardware and Bluetooth delays. Wired comparison approximates additional output delay. Current tests cover synthetic signals rather than a calibrated physical loopback. Cold-start versus steady continuous playback is not distinguished yet.
