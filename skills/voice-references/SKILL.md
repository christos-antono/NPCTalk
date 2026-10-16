---
name: voice-references
description: Make the reference clips a zero-shot voice cloner needs for each character (Fish Audio Drama 3 or S2 Pro, MiniMax, ElevenLabs-style cloning) from recordings the user has the rights to. Split into lines, pick clean 6-11 s runs, transcribe them, drop screams and noise, build a ~25 s clone clip plus single reference clips and mood clips (angry, drunk), check them, and upload them to the voice server. Use when adding a character's voice, when a cloned voice sounds off, thin or noisy, or when the user asks how to clone a voice for an NPC.
---

# Reference voices

A zero-shot cloner copies whatever is in the reference clip: the voice, but also the room, the music, the
mic and the mood. Most "the clone sounds wrong" problems are reference problems.

## Rights first
Clone only voices the user has the rights to: their own, an actor who agreed, a licensed or generated voice.
A game's characters are voiced by real people. For a private single-player experiment the user may decide to
use the game's own audio; it still never goes into a repo, a release or a public video without the rights.
`npctalk check` fails on audio files and `.lab` transcripts.

## Good source audio
- One speaker, no music or effects under the voice (game cutscenes usually have both).
- Normal speaking level for the neutral voice; shouting goes in an `angry` mood folder, slurring in `drunk`.
- A minute or more of lines with pauses between them. More lines means more candidates to pick from.
- If music is unavoidable, isolate vocals first (Demucs, as `server/finetune/prepare_dataset.py` does).

## Make the clips
```bash
npctalk voice make --name Narrator --audio narrator.wav                   # neutral voice
npctalk voice make --name Narrator --audio narrator_angry.wav --mood angry  # lines tagged [angry]
npctalk voice upload voices/Narrator                                      # -> the server's /voices/Narrator
```
What `voice make` does (the same steps built the GTA V example's 13 voices):
1. Split into lines at pauses of 0.3 s (20 ms frames louder than -42 dBFS are speech).
2. Join runs of up to 4 neighbouring lines into 6-11 s candidates; score them for normal level (about -20 dBFS,
   -15 for angry), even loudness, no clipping, few joins, ~9 s; keep the best 12 that don't overlap.
3. Transcribe each with fal speech-to-text (`FAL_KEY`), or on open weights with `--stt-url` (any OpenAI-compatible
   transcription server, e.g. `server/asr.py` running NVIDIA Nemotron ASR, or Whisper on the user's PC).
4. Drop candidates with fewer than 6 words or a speaking rate outside 1.4-4.8 words/s (screams, grunts, music,
   another language).
5. Write `ref_0..2` (the best three) and `clone.wav`: the best candidates joined up to ~25 s. Every clip has a
   `.lab` transcript next to it; the cloner needs the exact words.

```
voices/Narrator/clone.wav + clone.lab        what the server clones from by default ("Narrator/clone")
voices/Narrator/ref_0..2.wav + .lab          single clips, for engines that want a short one
voices/Narrator/angry/ref_0..1.wav + .lab    used when a line starts with [angry]
voices_work/Narrator/cand_*.wav + .lab       every candidate, to listen to (never commit)
```

## Which clip to clone from (measured)
On Drama 3, the ~30 s `clone.wav` gave 0.680 voice match on Lamar's test lines; each of his single 6-10 s clips
gave 0.610-0.630. Passing all four clips as references at once was no better than `clone.wav` alone. S2 Pro
preferred a single clean clip (0.707 vs 0.680), so test both if you switch models (skill
`voice-likeness-eval`).

## Check them
- **Listen to `clone.wav`.** Anything in it (a laugh, a car, a second voice) shows up in every line.
- **Read the `.lab`.** A wrong transcript makes the cloner mumble or skip words; fix the text by hand.
- **Say a test line:** `npctalk say "Testing, one two." --voice Narrator/clone --out t.wav`.
- **Moods:** keep 2 clips per mood. If an angry line sounds like a different person, the mood clips are too
  far from the neutral ones (another recording session or mic): drop them.

## Upload and use
The server reads `/voices` in its Modal volume when it starts, so restart it after an upload
(`modal app stop npcvoice-drama`). A game refers to a voice by folder and clip name (`Narrator/clone`); mood
folders are picked by the line's leading tag. Until a restart, a game can send the clip by URL with its
transcript (`reference_audio_url` + `reference_text`), at ~9 s extra the first time.
