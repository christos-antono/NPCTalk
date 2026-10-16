# GTA V: talk to anyone (the NpcVoice mod)

A ScriptHookVDotNet script for GTA V **story mode**. Walk up to anyone and just talk (open mic), press **B** to
type, or hold **N**: they answer in character, in a cloned voice, about 1-2 seconds later, with their mouth moving,
the line as a subtitle, and an action if their mood (or your order) calls for one. Voices are panned to where the
NPC is on screen and fade with distance.

This is the worked example for the skills in this repo; `docs/how-it-was-made.md` tells the story and the numbers.

```
mod/                 the script (C#, .NET Framework 4.8), NpcVoice.ini, personas in NpcVoice/characters/
sample-lines/        example style lines for Franklin (original writing, not game dialogue)
tools/               npcvoice-check (the network half without the game), shoot.ps1 + edit.ts (videos),
                     reload-mod.ps1, gta-front.ps1, gta-key.ps1, set-key.ps1
```

> **Single-player only.** Never launch GTA Online with Script Hook V or these files installed.

## Controls
| Key | What |
|---|---|
| (just talk) | Open mic: what you say goes to whoever is near you and in view, in or out of dev mode |
| **M** | Open mic off / on (the bottom-right tag says which) |
| **B** | Type what you say (on-screen keyboard) |
| **N** (hold) | Say it into your mic, release to send |
| **J** / **K** / **L** | Go to Michael's house / Franklin's house / Trevor's trailer, with the people who live there |
| **F9** | Dev mode (below) |
| **F10** | In dev mode: hide or show its panels, dev mode stays on |
| **F7** | Mod on / off. While it's off nobody answers, and the bottom-right tag says "NpcVoice off (F7)" |
| **F8** | Nearest NPC says an ambient line now (testing) |

All keys are in `NpcVoice.ini` (`[Keys]`).

## Install
1. **GTA V Legacy** (tested on 1.0.3889.0) + [Script Hook V](http://www.dev-c.com/gtav/scripthookv/) +
   [ScriptHookVDotNet](https://github.com/scripthookvdotnet/scripthookvdotnet) v3 (we used a 3.7 nightly). GTA V
   Enhanced needs a Script Hook V build for its exact version and ScriptHookVDotNet Enhanced.
2. **The voice server:** deploy `server/drama3.py` on Modal and make voices (skills `fish-voice-server` and
   `voice-references`). Note the URL `modal deploy` prints.
3. **Keys**, as Windows user environment variables (PowerShell, after copying a key):
   ```powershell
   [Environment]::SetEnvironmentVariable('FAL_KEY', (Get-Clipboard).Trim(), 'User')           # speech-to-text, MiniMax
   [Environment]::SetEnvironmentVariable('CEREBRAS_API_KEY', (Get-Clipboard).Trim(), 'User')  # the LLM
   [Environment]::SetEnvironmentVariable('TYPESAFE_API_KEY', (Get-Clipboard).Trim(), 'User')  # actions (Jev)
   [Environment]::SetEnvironmentVariable('NPCVOICE_TOKEN', (Get-Clipboard).Trim(), 'User')    # your voice server
   ```
   Open models only: skip fal, Cerebras and TypeSafe, and set `[Stt]`, `[Llm]`, `[Actions]` and `[Tts] Fallback`
   as in the README's [Open models](../../README.md#open-models) section.
4. **Build and copy:**
   ```powershell
   cd examples/gta-v/mod; dotnet build -c Release
   Copy-Item bin/Release/net48/* "<GTA V>/scripts/" -Recurse -Force
   Copy-Item -Recurse ../../../voices "<GTA V>/scripts/NpcVoice/voices"    # the voices you made, for the clip list and MiniMax
   ```
5. **Point the mod at your server:** in `<GTA V>/scripts/NpcVoice.ini`, `[Modal] Url = <workspace>--npcvoice-drama.modal.run`
   (no `https://`: ScriptHookVDotNet's ini reader cuts values at `//`; the mod adds it back).
6. In `<GTA V>/ScriptHookVDotNet.ini` set `ReloadKeyBinding=Insert`; press **Insert** in game to reload after an
   update (`tools/reload-mod.ps1` sends it for you, once the game is in front).
7. Optional: in game, Settings > Saving and Startup > Landing Page **Off**, Startup Flow **Story Mode**, to boot
   straight into the dev scene.

**If the mod doesn't load after an update**, check `<GTA V>/ScriptHookVDotNet.log`: Windows Smart App Control can
block a new unsigned DLL ("An Application Control policy has blocked this file"; Code Integrity event 3077). Test a
build before installing it (`[System.Reflection.Assembly]::LoadFile("<full path>\NpcVoice.dll")` in Windows
PowerShell throws if it's blocked), and keep the last build that loaded. Don't change the security setting for it.

## How a reply is made
speech-to-text (`[Stt]`, fal `nvidia/nemotron-asr-multilingual/asr`, ~0.4 s, audio sent inline, or NVIDIA Nemotron
ASR's open weights on your GPU with `Provider = openai`) → LLM in character with per-NPC history (`[Llm]`, Cerebras
`qwen-3.8-27b`, ~0.3 s, or any OpenAI-compatible server, local ones included) → voice (`[Tts] Engine`) → NAudio
playback. The action picker (`[Actions] Provider`: Jev, or the LLM) picks the action in parallel. A conversation reply cuts off any ambient line. Every exchange is logged with its
timings in `<GTA V>/scripts/NpcVoice/NpcVoice.log`.

| `[Tts] Engine` | First audio | Notes |
|---|---|---|
| `modal` (default) | ~0.4-0.5 s warm | Your Fish Audio server: **Drama 3** (`server/drama3.py`, app `npcvoice-drama`) or **S2 Pro** (`server/s2pro.py`), same API, streamed as it's generated, `[tags]` and mood clips. Needs `[Modal] Url` and `NPCVOICE_TOKEN`. The GPU stops 5 minutes after the last request (cold start 1-3 min), so the mod pings it every `KeepWarmSeconds` of silence while you play, and uses `Fallback` while it wakes |
| `minimax` | ~2.5-4.5 s | fal `minimax/speech-2.8-turbo`. Clones each voice once on first use from `clone.wav` ($1.50, id cached in `voices/<name>/minimax_voice.txt`). No GPU, no cold starts |
| `lux` | ~2.5 s warm | fal `lux-tts`, clones from `ref_*.wav` every call, very cheap; its content checker rejects a lot of GTA swearing |
| `fish` | ~0.4 s warm | S2 Pro on fal (`server/fal/fal_app.py`) over a realtime WebSocket; `[Fal] AppId` |

`[Tts] DirectorBestOf = 8` makes the director's pre-rendered lines use `best_of: 8` on Drama 3 (the best voice
likeness measured; skill `fish-voice-server`).

## Who you talk to
Whoever you last talked to while they're within 6 m and in front of you, otherwise the best person around: within
5 m always counts, 5-12 m only if the camera points at them; named characters first, then the nearest. Nobody
there: the log says `open mic -> nobody near` and the screen says "Listening... (to nobody near you)".

## Characters
`NpcVoice/characters/<PedHash>.txt`: header lines, `---`, then the persona that goes into the system prompt.
```
name: U.S. Marine
voice: Marine                     folder in voices/ (default: the file name)
models: Marine01SMM, Marine02SMY  other ped models that are this character too
slurs: no                         yes only if the source character uses them (off in every file here)
---
A hard-charging U.S. Marine ...
```
- `_npc.txt` is the persona for ordinary pedestrians; they get a voice from `[Voices] MalePool` / `FemalePool`.
- **Style lines:** `<PedHash>.examples.txt` (written examples, `situation|line`; Franklin's is
  `sample-lines/franklin.txt`) and `NpcVoice/lines/<Voice>.txt` (lines the character really says). Every prompt
  carries a fresh random 30 of them. Write your own, or for a private build extract them yourself; a game's
  dialogue never goes into a repo.
- The shared rules are `Dialogue.Rules`: short spoken lines, one leading delivery tag, GTA's M-rated tone, do what
  you're told, slurs only for `slurs: yes`, no racial insults or anti-gay slurs from anyone. Lines over 45 words
  end at the last full sentence.

## Voices
Made with `npctalk voice make` (skill `voice-references`) and uploaded to the server with `npctalk voice upload`;
the same folders go into `<GTA V>/scripts/NpcVoice/voices/`:
```
voices/Franklin/clone.wav + .lab     what the server clones from ("Franklin/clone")
voices/Franklin/ref_0..2.wav + .lab  single clips (Lux, fallbacks)
voices/Franklin/angry/, drunk/       used for lines tagged [angry] / [drunk]
```
Character files name their folder with `voice:`. The mod uploads the clips to fal storage when the game starts
(for engines that take a URL) and has the server say a word in every voice so the first real line is fast.

## Homes
**J**, **K** and **L** fade out and take you to Michael's living room, Franklin's house in Vinewood Hills, or outside
Trevor's trailer in Sandy Shores, with the people who live there in front of you (never a copy of who you're
playing). They're released back to the game once you're 200 m away. If an interior doesn't load you land outside
the front door (`visit:` in the log says which).

## Actions
NPCs act out what they say, and do what you tell them. While the LLM writes the reply, TypeSafe's **Jev**
(`[Actions]`, `TYPESAFE_API_KEY`), or with `Provider = llm` the open LLM in `[Llm]`, checks whether you gave an order ("take your shirt off", "get on your knees",
"give me a hug"); if so they do it and the reply is written again knowing it. Otherwise Jev picks from your words
and their reply, in parallel with the voice: punch, slap, headbutt, shoot, pull a gun, flip you off, facepalm,
square up, shirt off (men), pants down to underwear, bend over, hug, kiss, kneel and beg, run away, or nothing.
Each action has its own minimum probability (`ActionCatalog.cs`) and a one-minute cooldown unless ordered.
Animations are checked against the game's files at load (`action anims:` in the log). Outside dev mode you're not
invincible: a character who shoots you can kill you.

## Dev mode
**F9** (or at startup with `[Dev] AutoStart = true`): you land in Legion Square with every character in
`[Dev] Scene` in a ring around you. No police, no dying. The left panel lists every character (**Up/Down**,
**Enter** goes to them or spawns them, **Delete** rebuilds the scene); the right one shows the engines, the mod's
state and the last exchange's timings (speech-to-text, LLM, voice, action). A marker shows who you'd talk to.
**PgUp/PgDn** pick an action and **End** makes your target do it; **[** / **]** save their shirt-off / pants-off
look. **F10** hides both panels and keeps the rest.

## Check without the game
`tools/npcvoice-check` runs the same network code (uploads, LLM, voices, speech-to-text of the result, the action
picker) outside the game, with the settings in `mod/NpcVoice.ini`; `Section.Key=value` arguments override them:
```powershell
dotnet run -c Release --project tools/npcvoice-check -- <voices dir> minimax Franklin "You got a minute?"
dotnet run -c Release --project tools/npcvoice-check -- talk LamarDavis "take your shirt off"
dotnet run -c Release --project tools/npcvoice-check -- actions      # 25 exchanges, 10 of them orders: Jev 25/25
dotnet run -c Release --project tools/npcvoice-check -- actions llm  # the same through the open LLM: 24/25
dotnet run -c Release --project tools/npcvoice-check -- stt line.wav Stt.Provider=openai Stt.BaseUrl=127.0.0.1:8000/v1
dotnet run -c Release --project tools/npcvoice-check -- vad <voices dir>
```

## Recording videos
**Short gameplay clips** (`DirectorClips.cs`, skill `npc-showcase-clips`): a script whose blocks start with
`clip:` shoots one exchange per clip, 5-10 s, that looks like ordinary play: the gameplay camera held still behind
Franklin (hidden when the clip is between two NPCs), the HUD and the game's subtitles on, nothing of the mod on
screen. `at:` is a street spot (Grove St, Legion Square, Vinewood Blvd, Del Perro Pier, Simeon's, Sandy Shores,
Paleto Bay, LSIA...) or a home.
```
clip: Water pistol
at: grove
time: 21
a: player
b: LamarDavis
line: Lamar, put the gun away. It's a water pistol.
reply: insist it's still scary and he's the most feared man on Grove Street
```
**Longer scenes** (`Director.cs`): you say each `you:` line in your own character's cloned voice and the other
character answers live; text after `>>` steers how they react:
```
scene: Lamar's new jacket
place: franklin
with: LamarDavis
you: Nice jacket. Where'd you get it? >> brag that it's limited edition and roast Franklin's hoodie
```
Shoot and cut:
```powershell
powershell -ExecutionPolicy Bypass -File tools\shoot.ps1 -Out data\director\take1 -Script clips.txt -Reload
deno run -A tools/edit.ts --clips take1 --out data/director/clips      # one MP4 per clip, at most 10 s
deno run -A tools/edit.ts --out data/director/final --use take1:1-10   # scenes: 16:9 and 9:16 videos
```
`shoot.ps1` brings GTA to the front, reloads the mod, records the screen (ffmpeg `ddagrab`) and the PC's sound
(WASAPI loopback), starts the script, and restarts capture if focus is lost (GTA pauses). Don't touch the PC while
it shoots. Scripts and recordings stay in `data/` (ignored by git); clips carry cloned voices, so publishing one is
your call, labelled as AI.

## Gotchas
1. **Nobody answers, dev mode says nobody's targeted.** The mod is off (F7), or nobody is within 5 m / in front of
   the camera. The bottom-right tag shows the state; the log shows `open mic -> nobody near`.
2. **Every HTTPS call fails.** GTA5.exe makes .NET default to TLS 1.0; `Net.Init()` turns on TLS 1.2/1.3.
3. **A URL in the ini arrives as `https`.** ScriptHookVDotNet's ini reader cuts at `//`; write URLs without a scheme.
4. **Nothing ticks while you're in another window.** GTA pauses on focus loss, and so do scripts. Automation must
   bring it to the front (`tools/gta-front.ps1`) and send keys with SendInput scan codes (`tools/gta-key.ps1`);
   `PostMessage` doesn't reach the menus.
5. **The first line after a break takes a minute or two.** The GPU scaled to zero; `Fallback = minimax` covers it.
6. **The mod didn't load after an update.** Smart App Control (see Install).
