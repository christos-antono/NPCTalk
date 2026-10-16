---
game: Grand Theft Auto V (Legacy, PC, story mode)
title: Talking NPCs with Fish Audio
status: working
agents: [Claude Code (Opus 5.5)]
date: 2026-10-15
---

# Talking NPCs with Fish Audio

Walk up to anyone in story mode and talk out loud; they answer in character, in a cloned voice, 1.2-2.2 s after you stop (push-to-talk; +0.7 s with the open mic), with
an action when it fits. Code: `examples/gta-v`. Story: `docs/how-it-was-made.md`.

## Setup
- GTA V Legacy 1.0.3889.0, Script Hook V, ScriptHookVDotNet v3 (compiled against the 3.6.0 NuGet package, run on a
  3.7 nightly), .NET Framework 4.8, NAudio 2.2.1, Windows 11.
- Voice: Fish Audio Drama 3 (`fishaudio/drama-3`, FP8) via SGLang-Omni (commit `549dd1b9`) on a Modal H100;
  S2 Pro on the same API as the alternative. Fallback: MiniMax speech-2.8-turbo on fal.
- LLM: Cerebras `qwen-3.8-27b`, reasoning off. Speech-to-text: fal `nvidia/nemotron-asr-multilingual/asr`. Actions:
  TypeSafe Jev.
- Open-model options for each part (README, "Open models"): Nemotron 3.5 ASR's weights on a Modal L4
  (`server/asr.py`), Qwen3.8-27B on any OpenAI-compatible server, the LLM as the action picker, no closed fallback.

## Route into the game
A ScriptHookVDotNet script DLL in `<GTA V>/scripts/`: C#, hot-reloadable (Insert), full access to natives, the
standard route for single-player script mods. Audio plays on the PC, not through the game's sound engine.

## The four hooks
- **Who:** `World.GetNearbyPeds(player, 12f)`, humans and alive; within 5 m always, 5-12 m only when the gameplay
  camera points at them (dot > 0.85); named characters (by model hash) first; the last partner sticks within 6 m.
  Rescanned every 150 ms.
- **Scene:** `World.GetZoneLocalizedName`, `World.GetStreetName`, time of day, weather, the player's vehicle and
  weapon, wanted level, health, and whether it's the character's home.
- **Voice at the NPC:** NAudio playback of the streamed PCM, panned by `Screen.WorldToScreen`, faded 4-30 m;
  `PLAY_FACIAL_ANIM` (`mp_facial`/`mic_chatter`) while the audio is loud; `Screen.ShowSubtitle`.
- **Actions:** anim dictionaries and clips checked with `REQUEST_ANIM_DICT` at load (missing ones fall back), tasks
  for fleeing and fighting, component variations for the undress actions (underwear stays on).

## Timings (warm, from `NpcVoice.log`)
speech-to-text 0.38-0.80 s, LLM 0.28-1.0 s (occasional 5-8 s spikes), Jev 0.07-0.18 s in parallel, first audio
0.35-0.55 s; end of sentence to voice 1.15-2.2 s, plus the open mic's 0.7 s silence wait. Cold GPU: 1-3 min, covered by the MiniMax fallback.

## Verification
- Every exchange logged with per-step timings and what was heard and said.
- `tools/npcvoice-check` runs the network half (prompt, LLM, voices, speech-to-text of the result, Jev) outside
  the game; `npcvoice-check actions`: 25/25 expected picks.
- `npcvoice-check actions llm` (Qwen3.8-27B on Cerebras picking from first-token logprobs): 24/25.
- `npcvoice-check stt` against `server/asr.py`: the mod's OpenAI-compatible client, 0.52-0.98 s warm, 97 s cold.
- 20 gameplay-style clips recorded and checked frame by frame (contact sheets) and by ear.
- Voice likeness measured on one character (Lamar, 10 lines): Drama 3 best of 8 = 0.704 voice match (ECAPA vs
  60 held-out real clips), S2 Pro's best zero-shot 0.707, an S2 Pro fine-tune 0.785.
- **Not verified:** GTA V Enhanced; other characters' likeness by numbers (only by ear); long play sessions for
  memory growth; a play session with every part on open models (the pieces were checked outside the game); a local
  LLM server instead of Cerebras.

## Gotchas
1. **Every HTTPS call fails from the game.** GTA5.exe has no .NET entry assembly, so .NET defaults to TLS 1.0. Turn
   on TLS 1.2/1.3 at startup (`Net.Init`).
2. **A URL in the ini arrives as `https`.** ScriptHookVDotNet's ini reader treats `//` as a comment. Write URLs
   without the scheme and add it in code.
3. **Scripts stop when the game isn't in front.** GTA pauses on focus loss. Automation must bring it to the front;
   send keys as SendInput scan codes (PostMessage doesn't reach its menus).
4. **The mod doesn't load after an update.** Windows Smart App Control blocked the unsigned DLL (Code Integrity
   event 3077, "An Application Control policy has blocked this file" in `ScriptHookVDotNet.log`). Test-load builds
   with `[Reflection.Assembly]::LoadFile`; keep the last build that loaded; .NET builds are deterministic, so the
   same source rebuilds the same allowed DLL.
5. **Nobody answers / "no target" in dev mode.** The mod was switched off with F7; the notification vanished in
   seconds. The off state is now shown permanently in the corner.
6. **The open mic answers the room.** It hears calls and other people too. Mute key (M) and an on-screen mic state.
7. **A short line comes out as 47 s of rambling, or a 14-second whimper.** About 1 Drama 3 take in 10 runs to the
   token limit or trails off into noises. Cap reply length (45 words); use checked best-of for pre-rendered lines.
8. **A story phone call lands mid-recording.** Suppress the phone and help text every frame while a clip rolls.
9. **Never launch GTA Online with ScriptHookV installed.**

## Credits
Script Hook V (Alexander Blade), ScriptHookVDotNet, NAudio, Fish Audio (Drama 3, S2 Pro, fish-speech), SGLang-Omni,
Modal, fal, Cerebras, TypeSafe.
