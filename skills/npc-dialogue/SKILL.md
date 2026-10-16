---
name: npc-dialogue
description: Write what NPCs say with an LLM so they sound like themselves and like the game. Covers persona files, a fresh random sample of the character's own lines in every prompt, scene facts (place, time, weather, what the player holds or drives, danger), per-NPC conversation history, short spoken output with one delivery tag for the voice model, doing what the player tells them, fast models with reasoning off, and cleaning the output (refusals, name prefixes, length, slur rules). Use when NPC replies sound generic, robotic, too long, preachy or out of character, refuse, break the game's tone, or come back too slowly, or when adding a new character's persona.
---

# NPC dialogue

The voice can be perfect and the NPC still sounds like a chatbot. What fixes it is in the prompt: who they are,
how they really talk, where they are, and hard rules about the shape of a spoken line.

## The prompt (what `examples/gta-v/mod/Dialogue.cs` sends)
```
system:  RULES (shared by everyone; the game's tone and the shape of a line)
         You are <Name>. <persona>
         Lines you've actually said (situation|line): <30 random lines from their own list, fresh every call>
         Examples of how you talk: <8 written examples, optional>
         Right now: <scene facts>
history: the last exchanges with this NPC (per NPC, so each one remembers you)
user:    what the player said
```
- **The sample of real lines is what makes them sound like themselves.** A fresh random 30 each call stops
  them repeating catchphrases. Use lines you have the rights to: written by you in the character's style
  (`examples/gta-v/sample-lines/franklin.txt`), or the game's own lines kept locally if the user extracts them
  for a private build (`NpcVoice/lines/<Voice>.txt`, never committed).
- **Scene facts are short and concrete:** "Grove Street, Davis, 9pm, foggy. Franklin is holding a pistol and
  is wanted by the police (2 stars)." NPCs react to the gun, the weather, the car; that's what makes it feel
  like the game and not a chat window.
- **Personas** are a few lines each: who they are, how they talk (slang, catchphrases, what they swear like),
  who they love and hate. File format (`NpcVoice/characters/<Key>.txt`):
  ```
  name: Lamar Davis
  voice: LamarDavis                 folder in voices/ (default: the file name)
  models: ...                       other model names that are this character
  slurs: no                         yes only if the source character uses them (see Content)
  ---
  Franklin's loud, cocky best friend ... calls Franklin "busta", says "homie", "dog" ...
  ```
  `_npc.txt` is the persona for everyone without a file.

## Rules that shape a spoken line (keep these in any game)
- Only the words said out loud: no narration, asterisks, quotes, name prefix, emoji.
- Short: usually one or two sentences, 5-25 words; cap it (45 words) and cut at the last full sentence.
- At most one delivery tag, at the very start (`[angry]`, `[laughing]`, `[whispering]`...): the voice model acts
  it, and the game can pick a mood voice folder from it.
- React to what was said and to the scene; don't echo the player, don't end every line with a question.
- Stay in character; never mention being an AI or a game.
- **Do what you're told.** If the player tells them to do something physical, they do it and say what their
  character would say while doing it. With actions on, ask the action picker first whether it's an order; if
  it is, write the line again knowing "you're <doing it> right now" (skill `npc-actions`).

The **tone** rules are per game. GTA V's are M-rated: foul-mouthed, hostile, roasting the player back harder.
A cozy farming game wants the opposite. Write them for the game, in the game's register.

## Clean the output (never trust it raw)
`Dialogue.Clean` drops refusals (an empty reply, so an NPC never reads out a safety lecture), strips quotes,
`*actions*` and a `Name:` or `[Name]` prefix wherever the model put it, applies the content rules, and cuts to 45
words at the last full sentence.

## Content
- Racial slurs are replaced by default, from everyone. `slurs: yes` in a persona allows them only for a
  character who uses them in the source material, the way they do; every persona in this repo has it off.
- Insults about race (including "ape"/"monkey" jabs) and anti-gay slurs are blocked for everyone.
- Swearing follows the game's tone; profanity isn't a problem in an M-rated game.

## Speed
- Use a fast model and turn reasoning off: Cerebras `qwen-3.8-27b` answers in ~0.3 s (`DisableReasoning = true`).
  Reasoning models add 1-5 s, and the player hears every millisecond.
- The same open weights (Qwen3.8-27B, Apache 2.0) run on your own GPU: vLLM or SGLang (`vllm serve Qwen/Qwen3.8-27B`),
  or a GGUF quant in llama.cpp, Ollama or LM Studio. Point the OpenAI-compatible base URL at it (no key on
  localhost; write `127.0.0.1`, since on Windows `localhost` tries IPv6 first and the first request waits ~2 s). Qwen
  thinks by default there: switch it off through the chat template
  (`chat_template_kwargs: {"enable_thinking": false}`, `DisableReasoning = template`), and strip any `<think>` block
  that still comes back so it's never spoken. Measure the time per line; one consumer GPU is slower than Cerebras.
- `MaxTokens = 160`, `Temperature = 0.9`.
- Run the action picker in parallel with the LLM, not after it.
- Log the LLM's time per exchange; it occasionally spikes to 5-8 s. If that matters, fire a second request
  after ~2 s and take whichever answers first.

## Test without the game
`npcvoice-check talk <Character> "<what the player says>"` (GTA example) builds exactly the prompt the mod sends
and runs the LLM and the action picker; `npcvoice-check talk` runs a set of exchanges for several characters.
Read ten replies per character before tuning anything.
