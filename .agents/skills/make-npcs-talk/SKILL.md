---
name: make-npcs-talk
description: Make NPCs in a game talk back. The player speaks or types, the NPC answers in character, in a cloned voice, about 1-2 seconds later, and can act on what was said. Covers the whole loop and the order to build it in (who is being talked to, what the scene is, speech-to-text, an LLM in character, Fish Audio voices on a GPU, streamed playback from the NPC, subtitles, mouth movement, actions), the four hooks a game has to provide, latency budgets, costs, testing in the real game, and the rules (single-player, voices you have the rights to, nothing of the game shipped). Use when the user wants NPCs, characters, villagers, companions or enemies in any game to hold conversations, react to the player's voice, or be voiced by Fish Audio (S2 Pro or Drama 3) and an LLM.
---

# Make NPCs talk

The finished loop, as built for GTA V (`examples/gta-v`), with times measured in the game:

```
player's mic ──► voice activity ──► speech-to-text ──► who? + scene ──► LLM in character ──► voice server ──► NPC speaks
   (open mic,       (end of the       (fal Nemotron,     (the game)      (Cerebras, with      (Fish Audio on     (streamed PCM,
    push-to-talk,    sentence)         0.4-0.8 s)                         persona + lines,    an H100, first     panned, subtitles,
    or typing)                                                            0.3-1.0 s)          audio ~0.4 s)      mouth, action)
                                                                │
                                                                └──► action choice (Jev, ~0.1 s, in parallel) ──► animation
```
From the end of the player's sentence to the NPC's voice: 1.2-2.2 s warm with push-to-talk (logged exchanges:
1.15 s, 1.20 s, 1.57 s, 2.19 s); the open mic adds the 0.7 s of silence it waits for before deciding the sentence
ended. The LLM sometimes spikes to 5-8 s, so log every step's time per exchange.

## The loop: build it in this order
1. **Recon the game.** Version, how code runs inside it (script hook, mod loader, plugin API, or your own engine),
   single-player or not, how other mods find nearby characters and play sounds. Read `references/game-hooks.md`
   for the engine, and `knowledge/` for anyone who already did this game.
2. **Pick the engines** (table below). Default: Fish Audio Drama 3 on Modal for voices, Cerebras for the LLM, fal
   for speech-to-text. If the user wants open models only, every part has one (the "Open model" column).
3. **Stand the voice server up first, outside the game** (skill `fish-voice-server`): `npctalk health`, then
   `npctalk say "..." --voice Name/clone`. A game is the worst place to debug a GPU server.
4. **Make reference voices** for two or three characters (skill `voice-references`).
5. **Vertical slice in the game:** one NPC, typed text → LLM → voice → playback. Implement the four hooks below,
   nothing else. Log `stt / llm / tts / total` for every exchange.
6. **Voice input** (skill `voice-input`): push-to-talk first, then the open mic.
7. **Characters** (skill `npc-dialogue`): persona files, the character's own lines as style samples, scene
   facts, per-NPC history, output cleaning.
8. **Actions** (skill `npc-actions`): what they physically do, and doing what they're told.
9. **Verify in the real game**, with a dev mode that shows the target, the timings and the mod's state (below).
10. **Show it** (skill `npc-showcase-clips`), and if voice likeness matters, measure it (skill
    `voice-likeness-eval`) before claiming a setting is better.
11. **Write it down:** a field note in `knowledge/games/<game>/` (see `knowledge/README.md`).

## The four hooks a game has to provide
Everything else is game-free code. In the GTA V example about 2,450 of the mod's ~5,500 lines (dialogue, LLM,
voice clients, streaming playback, speech-to-text, open mic, action picking) build and run outside the game:
`examples/gta-v/tools/npcvoice-check` compiles exactly those files into a console app.

| Hook | What it returns | GTA V (ScriptHookVDotNet) |
|---|---|---|
| **Who is being talked to** | The NPC, or nobody | Living humans within 12 m; within 5 m always counts, 5-12 m only if the camera points at them (dot > 0.85). Named characters win ties; the last partner sticks while within 6 m |
| **Scene facts** | A few short strings | Zone and street name, time, weather, the player's vehicle and weapon, wanted level, health, whether this is the NPC's home |
| **Voice at the NPC** | Play streamed PCM from that NPC | NAudio playback, panned by screen position, faded 4-30 m, mouth driven by the audio level, the game's subtitle style |
| **Actions** | Name → animation or behaviour | Anim dictionaries checked at load (a missing one falls back), clothing swaps, tasks (flee, fight, kneel) |

Keep the hooks thin. If a game exposes them, the rest of this repo's code carries over.

## Choosing engines
| Need | Default | Open model (host it yourself) | Other alternatives |
|---|---|---|---|
| Voice, realtime | **Fish Audio Drama 3** self-hosted on Modal (`server/drama3.py`): first audio ~0.4 s warm, acts `[tags]` well | The default is open weights already; **S2 Pro** (`server/s2pro.py`, smaller, fine-tunable) too | MiniMax via fal (closed; no GPU to run, $1.50 per voice clone, 2.5-4.5 s per line) |
| Voice while the GPU wakes | MiniMax on fal | None needed: wait for the GPU (`Fallback =` empty), or S2 Pro on fal (`fish`) | |
| Voice, pre-rendered (cutscenes, demos) | Drama 3 with `best_of: 8` (best likeness measured) | Same | ElevenLabs (closed, scored lowest here, 0.612) |
| LLM | Cerebras `qwen-3.8-27b`, reasoning off (~0.3 s) | Qwen3.8-27B's weights (Apache 2.0) on vLLM, SGLang, llama.cpp, Ollama or LM Studio; local servers need no key; `DisableReasoning = template` turns Qwen's thinking off | Any OpenAI-compatible endpoint; reasoning models add seconds |
| Speech-to-text | fal `nvidia/nemotron-asr-multilingual/asr` (~0.4 s, audio inline) | Nemotron 3.5 ASR's weights (OpenMDW-1.1) on your GPU: `server/asr.py`, OpenAI-compatible, 0.5-1.0 s warm; Whisper or Parakeet behind any OpenAI-compatible server | |
| Actions | TypeSafe Jev (typed choice + probability, ~0.1 s) | The dialogue LLM picks from lettered options, with each option's probability taken from the first token's logprobs (24/25 vs Jev's 25/25 on Qwen3.8-27B, 0.2-1.5 s) | |

Licences of the open weights: Nemotron 3.5 ASR (OpenMDW-1.1) and Qwen3.8-27B (Apache 2.0) allow commercial use;
Drama 3 (Fish Audio Community License) is free for non-commercial use and for commercial use under US$500k a year
in revenue, not as a hosted service; S2 Pro (Fish Audio Research License) is non-commercial only. Tell the user
before they ship a game with them.

Costs: an H100 runs about $4/hour while it's up; it scales to zero 5 minutes after the last request, and the next
request waits 1-3 minutes for a cold start. The game pings the server while it runs, so it stays warm exactly
while someone plays (`server/keepwarm.py` holds it up for a demo without the game).

## Verify like you mean it
- **Log every exchange** with each step's time and what was heard and said. Most "it doesn't work" reports are
  answered by the log (in GTA V: "open mic -> nobody near" 755 times = the target lookup found nobody).
- **Show the mod's state on screen.** A mod switched off with a hotkey looks exactly like a broken one; say
  "off" somewhere visible. Show who would be talked to (a marker) in a dev mode.
- **Test the network half without the game** (`npcvoice-check` in the GTA example, `npctalk say` for the
  server).
- **Measure, don't eyeball, voice likeness** (skill `voice-likeness-eval`). Two runs of ten lines fooled us
  once; ten runs didn't.

## Rules
- **Single-player, or servers you run.** Never inject into online games with anti-cheat (for GTA V: never
  launch GTA Online with a script hook installed).
- **Voices you have the rights to.** Your own, an actor who agreed, a licensed or generated voice. Cloning
  a game's characters for your own single-player experiment is one thing; publishing those clones is another.
  Never commit voice clips, game audio, or the game's dialogue (`npctalk check` blocks audio and transcripts).
- **No secrets in the repo.** Keys come from environment variables. A server URL is a secret too when its
  `/health` needs no token: anyone with it can keep your GPU awake.
- **Content.** NPCs swear if the game's characters do. Slurs are filtered by default and opt-in per persona
  (skill `npc-dialogue`).
- **Ask first** before driving the user's keyboard and mouse in a running game, installing into game
  folders, or publishing anything.

Full reasoning: `references/safety.md`. Architecture and file map: `references/architecture.md`.
