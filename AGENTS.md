# NPCTalk

A [Universal Modder](https://github.com/rehan-remade/universal-modder) project. Community: the [Universal Modder Discord](https://discord.com/invite/sEZWaf7vV).

Skills, a self-hosted Fish Audio voice server and tools for AI coding agents (Claude Code, Codex, Cursor, Gemini
CLI, GitHub Copilot, OpenCode and anything else that reads `AGENTS.md`). When someone opens an agent in this
repo, they almost always want to **make the NPCs in a game talk**: the player speaks, the NPC answers in
character, in a cloned voice, about a second later, and acts on what was said. Or they want to learn how the GTA
V example was made.

## Start here
1. **Read `skills/make-npcs-talk/SKILL.md` and follow its loop:** recon the game → pick engines → voice server
   first, outside the game → reference voices → one NPC answering typed text in the game → voice input →
   characters → actions → verify in the real game → show it → write a field note.
2. **Read the worked example** before inventing anything: `examples/gta-v/` (the mod, its tools and README) and
   `docs/how-it-was-made.md` (what was tried, what was measured, what went wrong).
3. **Check `knowledge/`** for a field note on the game; write one at the end (`knowledge/README.md`).

## Skills (`skills/*/SKILL.md`, Agent Skills format)
| Skill | Use it for |
|---|---|
| `make-npcs-talk` | The whole loop, the four hooks a game must provide, engine choices, latency, rules |
| `fish-voice-server` | Hosting Fish Audio Drama 3 / S2 Pro on Modal (or fal): API, keep-warm, best-of, fine-tune, gotchas |
| `voice-references` | Reference clips from recordings the user has the rights to; `npctalk voice make/upload` |
| `npc-dialogue` | Personas, the prompt, scene facts, output cleaning, content rules, speed |
| `npc-actions` | What NPCs physically do, and following spoken orders (TypeSafe Jev, or the open LLM) |
| `voice-input` | Open mic, push-to-talk, speech-to-text, echo and noise |
| `voice-likeness-eval` | Measuring whether a voice change really helped |
| `npc-showcase-clips` | Gameplay-style clips of conversations |

Copies of `skills/` sit where agents look for them in a clone: `.agents/skills` (Codex, Gemini CLI, Copilot,
Cursor, OpenCode) and `.claude/skills` (Claude Code). Edit `skills/`, then copy it over; a test fails while the
copies differ.

## Tools
- **`bin/npctalk`** (Python, sets itself up with `uv`; `bin\npctalk.cmd` on Windows). Put `bin` on PATH, or
  `uv tool install git+https://github.com/christos-antono/NPCTalk`. Every command has `--help`:
  - `health`, `say`: talk to the voice server like a game does (URL from `NPCVOICE_URL`, token from
    `NPCVOICE_TOKEN`)
  - `voice make`, `voice upload`: reference voices from a recording, into the server's volume (`FAL_KEY` for
    transcription, or `--stt-url` for any OpenAI-compatible server such as `server/asr.py`; ffmpeg on PATH)
  - `check`: nothing that mustn't ship (audio, transcripts, keys, real server URLs)
- **`server/`**: Modal apps. `drama3.py` (default voice server), `s2pro.py`, `asr.py` (NVIDIA Nemotron ASR's open
  weights, OpenAI-compatible speech-to-text), `keepwarm.py`, `fal/` (S2 Pro on fal), `finetune/` (dataset, LoRA, the
  scorer). Run them with the `modal` CLI.
- **Open models only?** Every part has an open-weights option you can host (README, "Open models"); when the user
  asks for it, use those and say which numbers were measured on them and which weren't.
- **fal MCP server** (`https://mcp.fal.ai/mcp`, header `Authorization: Bearer $FAL_KEY`), pre-configured per agent:
  `.mcp.json`, `.codex/config.toml`, `.cursor/mcp.json`, `.vscode/mcp.json`, `gemini-extension.json`,
  `opencode.json`. Useful for speech-to-text, MiniMax voices and anything else on fal.
- **The GTA V example's tools** (`examples/gta-v/tools/`): `npcvoice-check` (the mod's network half without the
  game), `shoot.ps1` + `edit.ts` (recording and cutting clips), `reload-mod.ps1`.

## Rules (full reasoning in `skills/make-npcs-talk/references/safety.md`)
- **Where:** single-player games, or servers the user runs. Never inject into online games with anti-cheat (GTA V:
  never launch GTA Online with ScriptHookV installed). No anti-cheat, DRM or ownership bypasses.
- **Voices:** clone only voices the user has the rights to. Never commit or publish voice clips, cloned renders,
  game audio or a game's dialogue. Run `npctalk check .` before every commit you're asked to make.
- **Secrets:** keys from environment variables only. A voice server URL is a secret too (its `/health` needs no
  token and wakes the GPU); write `your-workspace` in examples.
- **Money:** say what a GPU kept up will cost before keeping it up (an H100 is about $4/hour).
- **Content:** slurs off by default and opt-in per persona; no anti-gay slurs or racial insults from anyone;
  undressing stops at underwear.
- **Ask first** before driving the user's keyboard and mouse in a running game (never while they're typing),
  installing into a game folder, or publishing anything (repos, PRs, videos, posts).
- **Measure** voice changes before claiming them (skill `voice-likeness-eval`), and keep a journal (`NPCLOG.md` in
  the working folder) that becomes your field note.

## Working on the toolkit itself
- **Python:** 3.10+, numpy only (`uv` handles it via `bin/npctalk`). One module per command group in `npctalk/`,
  each with `register(sub)` and a docstring that doubles as `--help`.
- **Tests:** `uv run --with pytest pytest -q tests`. CI also runs every `--help` screen and `npctalk check .`.
- **C# example:** `dotnet build -c Release` in `examples/gta-v/mod` and in `examples/gta-v/tools/npcvoice-check`.
- **Wording:** keep skills and docs agent-neutral ("the agent"), except in sections about one specific agent.
