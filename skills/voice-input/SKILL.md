---
name: voice-input
description: Let the player talk to NPCs out loud. Covers an always-on open mic with voice activity detection against the room's noise floor, push-to-talk and typing as fallbacks, ignoring the NPC's own voice coming out of the speakers, dropping coughs, clicks and filler words, fast speech-to-text (fal's Nemotron ASR, ~0.4 s, audio sent inline), and routing what was heard to the right NPC. Use when building or debugging mic input for a game, when the open mic triggers on noise or misses speech, when NPCs answer things the player didn't say, or when "nobody answers".
---

# Voice input

Three ways in, all ending in the same exchange: **open mic** (just talk), **push-to-talk** (hold N), **type** (B).
Build push-to-talk first; it has no detection problems. Then the open mic, which is what makes it feel like
talking to people.

## Open mic (`examples/gta-v/mod/OpenMic.cs`)
- 16 kHz mono capture in 30 ms blocks.
- **Noise floor:** the quietest moments of the last 1.5 s. An utterance starts when the level stays
  `SensitivityDb` (12 dB) above that floor for 150 ms, and ends after `SilenceMs` (700 ms) below it. The floor
  adapts to the room and to the game's own sound.
- Keep 300 ms from before the start, so the first syllable isn't cut.
- Drop anything shorter than 450 ms (coughs, clicks) and anything over 12 s without a pause (music, a loud game).
- **Pause while an NPC talks**, and for 600 ms after: their voice comes out of the speakers and would be
  transcribed as the player's. Also pause while push-to-talk is held, while typing, and while a reply is being
  written. Headphones make this much easier.
- **Drop non-speech after transcribing:** one-word fillers ("uh", "um", "hmm", "yeah", "okay", "thanks"...) and
  empty transcripts are ignored (`open mic ignored, not speech:` in the log).

## Who it goes to
Whoever the target lookup returns at that moment (skill `make-npcs-talk`, hook 1): within 5 m, or looked at
within 12 m; the last partner sticks while close. If nobody's there, log it ("open mic -> nobody near") and
show it on screen ("Listening... (to nobody near you)"), or the player thinks the mic is broken.

## Speech-to-text
fal `nvidia/nemotron-asr-multilingual/asr`: send the WAV inline as a data URI (no upload step), ~0.4 s for a short
sentence (0.38-0.80 s logged). Returns `output` or `text`.

Open weights on your own hardware: any OpenAI-compatible transcription server (`POST <base>/audio/transcriptions`,
multipart `file` + `model` + `language`, returns `text`). `server/asr.py` serves NVIDIA Nemotron 3.5 ASR
(`nvidia/nemotron-3.5-asr-streaming-0.6b`, open weights, OpenMDW-1.1) that way on a Modal L4: 0.52-0.98 s warm
through the mod's client, 97 s from cold (keep-warm doesn't reach it). Whisper or Parakeet on the player's PC work
the same way behind a server such as speaches. In the GTA example: `[Stt] Provider = openai`, `BaseUrl`, `Model`,
`ApiKeyEnv`; test with `npcvoice-check stt <file> Stt.Provider=openai Stt.BaseUrl=...`. Whatever you use, measure it
per exchange.

## Things that look like bugs
- **"Nobody answers"** with people around: is the mod on? A toggle key that switches everything off looks
  exactly like a broken mic. Show the off state on screen. Then check the target rules (are they within 5 m or
  in front of the camera?).
- **The NPC answers the room.** The open mic hears everything: a call, music, someone else talking. Give the
  player a mute key (M) and show the mic state on screen.
- **It triggers on the game.** Raise `SensitivityDb`, or use headphones.
- **First word missing:** the pre-roll is too short or the start threshold too slow.

## Test without the game
`npcvoice-check vad <voices folder>` runs the detector on real voice clips mixed over quiet and loud noise, and
reports what it caught and cut.
