<h1 align="center">NPCTalk</h1>

<p align="center">
  <b>By <a href="https://github.com/rehan-remade/universal-modder">Universal Modder</a></b>, the toolkit that lets AI agents mod almost any game you own ·
  <a href="https://discord.com/invite/sEZWaf7vV"><b>Join the Discord</b></a>
</p>

<p align="center">
  <b>Make any NPC in any game talk back, with Fish Audio voices.</b><br>
  Skills, a self-hosted voice server and tools that let an AI coding agent wire it up: the player speaks, the NPC
  answers in character, in a cloned voice, about a second later, and acts on what was said.
</p>

<p align="center">
  <a href="#install">Claude Code · Codex · Cursor · Gemini CLI · Copilot · OpenCode</a> ·
  <a href="#open-models">open models</a> ·
  <a href="docs/how-it-was-made.md">how it was made</a> ·
  <a href="examples/gta-v">GTA V example</a> ·
  <a href="https://discord.com/invite/sEZWaf7vV">Discord</a> ·
  <a href="LICENSE">MIT</a>
</p>

<p align="center"><img src="docs/media/architecture.svg" alt="One exchange: the player talks; voice activity detection; speech-to-text on fal; the game says who is being talked to and what's around; an LLM on Cerebras writes the reply in character while TypeSafe Jev picks an action; the Fish Audio voice server on a Modal H100 streams the cloned voice back; the NPC speaks, with subtitles, mouth movement and the action. End of sentence to voice: 1.2-2.2 seconds with push-to-talk; the open mic adds a 0.7 second wait." width="100%"></p>

Built first for **GTA V**: walk up to anyone in Los Santos and just talk. Lamar roasts your hoodie, a cop tells
you to file a civil claim and flips you off, Michael defends his robe like it's a business suit. Every line is
written live in character and spoken in that character's cloned voice by **Fish Audio Drama 3** (or **S2 Pro**),
self-hosted on a serverless GPU. Most of the code doesn't know it's in GTA, so the same pieces work in other
games: the skills say what a game has to provide, and how to build the rest.

**Open models all the way through:** NVIDIA Nemotron ASR hears you, Qwen3.8-27B writes the line, Fish Audio Drama 3
speaks it. Each is open weights you can run yourself, and so is everything else in the loop
([Open models](#open-models)).

## Install
Pick your agent. Each gets the same skills (Agent Skills format), the fal MCP server and the `npctalk` CLI.

| Agent | Install |
|---|---|
| **Claude Code** | `/plugin marketplace add christos-antono/NPCTalk`<br>`/plugin install npctalk@npctalk` |
| **Codex** | `codex plugin marketplace add christos-antono/NPCTalk`<br>`codex plugin add npctalk@npctalk` |
| **Gemini CLI** | `gemini extensions install https://github.com/christos-antono/NPCTalk` |
| **VS Code / Copilot** | Enable `chat.plugins.enabled`, run **Chat: Install Plugin From Source**, enter this repo's URL |
| **Cursor** | Clone it (Cursor reads `AGENTS.md` and `.cursor/mcp.json`) |
| **OpenCode** | Clone and run `opencode` inside it (`opencode.json` adds the skills and the fal MCP server) |
| **Skills only** (any agent) | `npx skills add https://github.com/christos-antono/NPCTalk` |
| **Anything else** | `git clone https://github.com/christos-antono/NPCTalk` and start your agent inside it |

Inside a clone, `.agents/skills` and `.claude/skills` are copies of `skills/`, and `AGENTS.md` holds the
instructions (`CLAUDE.md` and `GEMINI.md` point to it).

**The `npctalk` CLI.** Plugin installs and clones put it on PATH (`bin/`). Anywhere else:
`uv tool install git+https://github.com/christos-antono/NPCTalk`.

**Accounts and keys** (environment variables, never files):

| What for | Service | Variable |
|---|---|---|
| The voice server (GPU) | [Modal](https://modal.com) | `modal setup`; `NPCVOICE_TOKEN` = the value you put in the `npcvoice-auth` secret |
| Speech-to-text, MiniMax voices, the MCP server | [fal](https://fal.ai/dashboard/keys) | `FAL_KEY` |
| The NPC's lines (any OpenAI-compatible LLM) | [Cerebras](https://cloud.cerebras.ai) | `CEREBRAS_API_KEY` |
| Actions and spoken orders | [TypeSafe](https://docs.typesafe.ai) | `TYPESAFE_API_KEY` |

fal, Cerebras and TypeSafe each have an open replacement you can host yourself ([Open models](#open-models)).

You also need Git, Python 3.10+, `uv` and ffmpeg. The GTA V example needs Windows, the .NET SDK (8+) to build it,
and the game.

## Open models
Every part of the loop runs on an open model you can host yourself. The defaults use hosted services because they
are the fastest way to a working build, and the voices are open weights on your own GPU from day one.

| Part | Default | Open model | Run it yourself |
|---|---|---|---|
| **Speech-to-text** | NVIDIA Nemotron ASR on fal | [Nemotron 3.5 ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b), 0.6B, OpenMDW-1.1 | [`server/asr.py`](server/asr.py) (Modal, L4, OpenAI-compatible), or Whisper / Parakeet on your PC behind any OpenAI-compatible server; `[Stt] Provider = openai` |
| **The lines (LLM)** | Qwen3.8-27B on Cerebras | [Qwen3.8-27B](https://huggingface.co/Qwen/Qwen3.8-27B), Apache 2.0: already open weights | vLLM or SGLang (`vllm serve Qwen/Qwen3.8-27B`), llama.cpp, Ollama or LM Studio; `[Llm] BaseUrl = 127.0.0.1:8000/v1`, no key |
| **Voices** | Fish Audio Drama 3 on your Modal GPU | [Drama 3](https://huggingface.co/fishaudio/drama-3) and S2 Pro, open weights | Already self-hosted: [`server/drama3.py`](server/drama3.py) and [`server/s2pro.py`](server/s2pro.py) run them through SGLang-Omni on any H100-class GPU |
| **Voice while the GPU wakes** | MiniMax on fal (closed) | S2 Pro | `[Tts] Fallback =` empty (wait for your GPU), or `fish` (S2 Pro on fal, [`server/fal/fal_app.py`](server/fal/fal_app.py)) |
| **Actions** | TypeSafe Jev (closed) | The same LLM | `[Actions] Provider = llm`: the model's token probabilities pick the action |
| **Reference voices** (`npctalk voice make`) | Nemotron ASR on fal | Nemotron 3.5 ASR, Whisper | `--stt-url https://your-workspace--npcvoice-asr.modal.run/v1` (or a local server) |
| **Measuring voices** | Open already | Whisper large-v3, ECAPA (SpeechBrain), WavLM | [`server/finetune/train.py`](server/finetune/train.py) |

ElevenLabs appears only as a row in the voice comparison; nothing in the mod uses it.

**All open, in `NpcVoice.ini`** (the GTA V example; the same settings carry over to other games):
```ini
[Tts]
Engine = modal
Fallback =

[Llm]
; vLLM or SGLang on this PC serving Qwen/Qwen3.8-27B (http:// is added for local addresses; 127.0.0.1, because
; localhost makes Windows try IPv6 first and costs ~2 s on the first request)
BaseUrl = 127.0.0.1:8000/v1
Model = Qwen/Qwen3.8-27B
DisableReasoning = template

[Stt]
; server/asr.py: NVIDIA Nemotron ASR's open weights
Provider = openai
BaseUrl = your-workspace--npcvoice-asr.modal.run/v1
ApiKeyEnv = NPCVOICE_TOKEN

[Actions]
Provider = llm
```

**Measured:**
- `server/asr.py` through the mod's own client (`npcvoice-check stt`) took 0.52-0.98 s warm (fal: 0.68-2.0 s on the
  same clip). A cold start took 97 s, and the mod's keep-warm pings only the voice server.
- The LLM action picker with Qwen3.8-27B on Cerebras got 24/25 on `npcvoice-check actions` (Jev: 25/25), taking
  0.16-1.55 s per pick against Jev's 0.07-0.28 s. Its miss was "I'll put a bullet in your skull", split between
  shoot and threaten_gun. Two runs of the LLM picker gave the same picks.

**Not verified yet:** Qwen on a local vLLM, llama.cpp, Ollama or LM Studio (`DisableReasoning = template`; Ollama
switches thinking off its own way), Whisper and Parakeet servers, and a play session in the game with every part
open. A 27B model on one consumer GPU will answer slower than Cerebras; measure it before you rely on it.

**Licences of the weights:**

| Model | Licence | Commercial use |
|---|---|---|
| Nemotron 3.5 ASR | OpenMDW-1.1 | Yes |
| Qwen3.8-27B | Apache 2.0 | Yes |
| Fish Audio Drama 3 | Fish Audio Community License | Free for research and non-commercial use; commercial use under US$500k annual revenue, not as a hosted service |
| Fish Audio S2 Pro | Fish Audio Research License | Research and non-commercial only; commercial use needs a licence from Fish Audio |

Read each licence yourself before you ship anything.

## Try it
> Make the villagers in my game answer when I talk to them, in their own voices.

> Set up a Fish Audio Drama 3 voice server on Modal and clone a voice from `narrator.wav`.

> In GTA V story mode, let me talk to anyone near me with my mic.

> My NPC's cloned voice sounds thin. Is it the reference clip or the model? Measure it.

> Record five 8-second clips of NPCs arguing that look like normal gameplay.

The agent starts with the **make-npcs-talk** skill and runs the same loop every time:
1. recon the game: version, how code runs in it, single-player or not;
2. pick the engines (voice, LLM, speech-to-text, actions);
3. stand up the voice server first, outside the game, and test it with `npctalk say`;
4. make reference voices from recordings you have the rights to;
5. one NPC answering typed text in the game: the four hooks, nothing else;
6. voice input (push-to-talk, then the open mic);
7. characters: personas, their own lines, scene facts;
8. actions, and doing what they're told;
9. verify in the real game, with every step's timing logged;
10. show it, and measure the voices before claiming anything;
11. write a field note for the next agent.

## What's inside

<p align="center"><img src="docs/media/system.svg" alt="The repo: AI coding agents install it through plugin manifests, AGENTS.md and the fal MCP. Inside: 8 skills (how an agent builds it), the npctalk CLI (health, say, voice make/upload, check), server/ (Fish Audio Drama 3 and S2 Pro on Modal, keep-warm, fal app, fine-tune and scorer), examples/gta-v (the mod: 4 game hooks plus a game-free core, and its tools), and docs plus knowledge (how it was made, field notes). The agent builds the four hooks into your game, copying the GTA example. Paid services: fal (speech-to-text, MiniMax fallback), Modal (the GPU running Fish Audio's open weights), Cerebras (the LLM, swappable) and TypeSafe (actions, optional)." width="100%"></p>

**Skills** (`skills/`, Agent Skills format)

| Skill | What it does |
|---|---|
| [`make-npcs-talk`](skills/make-npcs-talk) | The whole loop, the four hooks a game must provide (who's talked to, scene facts, voice at the NPC, actions), engine choices, latency budget, costs, verification, rules; per-engine notes for GTA V, Unity, Unreal, Bethesda, Minecraft and your own game |
| [`fish-voice-server`](skills/fish-voice-server) | Host Fish Audio **Drama 3** or **S2 Pro** on Modal (or fal) behind one streaming API; settings that measurably help likeness; best-of-N for pre-rendered lines; keep-warm and cost; the S2 Pro fine-tune; every gotcha |
| [`voice-references`](skills/voice-references) | From a recording you have the rights to, to clean reference clips, a 25 s clone clip and mood clips, on the server |
| [`npc-dialogue`](skills/npc-dialogue) | Personas, a fresh sample of the character's own lines in every prompt, scene facts, short spoken lines with delivery tags, output cleaning, content rules, speed |
| [`npc-actions`](skills/npc-actions) | What NPCs physically do while they talk, and following spoken orders, with TypeSafe Jev; per-action thresholds; mapping to animations |
| [`voice-input`](skills/voice-input) | Open mic with voice activity detection, push-to-talk, speech-to-text, the NPC's own voice and the room's noise |
| [`voice-likeness-eval`](skills/voice-likeness-eval) | Measure whether a voice change really helped: speaker similarity to held-out real clips, word error, enough runs, independent checks |
| [`npc-showcase-clips`](skills/npc-showcase-clips) | Gameplay-style clips of conversations: fixed gameplay camera, lines rendered first, sync, 5-10 s cuts |

**The `npctalk` CLI** (Python). Every command has `--help` with examples.

| Command | What it does |
|---|---|
| `npctalk health` | Is the voice server up? Waits through a cold start |
| `npctalk say` | Speak a line in a voice, like a game does; prints time to first audio, writes a WAV |
| `npctalk voice make` | Recording → `voices/<Name>/clone.wav`, `ref_0..2`, mood clips, with transcripts |
| `npctalk voice upload` | A voice folder → the server's Modal volume |
| `npctalk check` | Blocks what must never ship: audio, transcripts, keys, real server URLs |

**The servers** (`server/`): `drama3.py` (Fish Audio Drama 3, the default), `s2pro.py` (S2 Pro, with
fine-tuned voices), `asr.py` (NVIDIA Nemotron ASR's open weights, OpenAI-compatible speech-to-text), `keepwarm.py`,
`fal/` (S2 Pro on fal over a realtime WebSocket), `finetune/` (dataset, LoRA, and the scorer behind every number
here).

**The GTA V example** ([`examples/gta-v`](examples/gta-v)): the full mod (open mic, any NPC, 19 characters, homes,
actions, dev mode, a director for recording clips), its tools, and a
[field note](knowledge/games/gta-v/talking-npcs-with-fish-audio.md).

**Field notes** ([`knowledge/`](knowledge)): one per game where NPCs were made to talk. Add yours.

## How good are the voices?
Measured on one character (Lamar, ten test lines): voice match is speaker similarity to 60 real clips that were
never used as references. Details and the method: [`skills/voice-likeness-eval`](skills/voice-likeness-eval).

| Setup | Voice match | Word error |
|---|---|---|
| ElevenLabs instant clone | 0.612 | 10.5% |
| Fish Audio Drama 3, default settings | 0.643 | 7.1% |
| Drama 3, tuned sampling, one take | 0.660 | 7.9% |
| **Drama 3, best of 8 takes, checked** | **0.704** | 8.6% |
| Fish Audio S2 Pro, its best reference clip | 0.707 | 5.6% |
| S2 Pro fine-tuned on the character (needs training) | 0.785 | 7.5% |

Without training, Drama 3 and S2 Pro tie on likeness; Drama 3 acts `[crying]` and `[laughing]` better and starts
talking faster. One early result here didn't survive a second run:
[how that was caught](docs/how-it-was-made.md#6-making-the-voice-sound-right-and-being-honest-about-it).

## Rules it follows
- **Single-player games, or servers you run.** It never injects into online games with anti-cheat (GTA V: never
  launch GTA Online with a script hook installed) and never bypasses anti-cheat, DRM or ownership checks.
- **Voices you have the rights to.** It never commits or publishes voice clips, cloned voices, game audio or a
  game's dialogue; `npctalk check` blocks them.
- **No secrets in files.** Keys come from environment variables, and voice server URLs stay private (their
  `/health` wakes the GPU).
- **It asks before** driving your keyboard and mouse in a game, installing into game folders, keeping a GPU up,
  or publishing anything.
- **It measures before it claims** a voice setting is better.

Full reasoning: [`skills/make-npcs-talk/references/safety.md`](skills/make-npcs-talk/references/safety.md).

## Community
NPCTalk is a [Universal Modder](https://github.com/rehan-remade/universal-modder) project. Universal Modder's skills and tools cover the rest of a mod (engine
recon, decompiling, fal-generated art, 3D and audio, in-game testing, showcase videos), and NPCTalk adds NPCs that
talk back. Install both and an agent can take a game from "can I mod this?" to characters you can talk to.

Show what you made, ask for help, and follow along in the **[Universal Modder Discord](https://discord.com/invite/sEZWaf7vV)**.

## Credits
- Voices: [Fish Audio](https://fish.audio) (Drama 3, S2 Pro, fish-speech), served with
  [SGLang-Omni](https://github.com/sgl-project/sglang-omni) on [Modal](https://modal.com) and [fal](https://fal.ai).
- Lines: [Qwen](https://huggingface.co/Qwen/Qwen3.8-27B) on [Cerebras](https://cerebras.ai). Speech-to-text:
  [NVIDIA Nemotron ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b), on fal or self-hosted.
  Actions: [TypeSafe](https://docs.typesafe.ai) Jev, or the LLM.
- GTA V: [Script Hook V](http://www.dev-c.com/gtav/scripthookv/),
  [ScriptHookVDotNet](https://github.com/scripthookvdotnet/scripthookvdotnet), [NAudio](https://github.com/naudio/NAudio).
- A [Universal Modder](https://github.com/rehan-remade/universal-modder) project; the repo layout and principles come from universal-modder.
- Built in real sessions with an AI coding agent (Claude Code), from the first line of the mod to the measurements.

MIT licensed. GTA V and its characters belong to Rockstar Games; this project isn't affiliated with Rockstar, Fish
Audio or any service above.
