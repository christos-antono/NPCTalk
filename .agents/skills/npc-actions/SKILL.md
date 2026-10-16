---
name: npc-actions
description: Make NPCs physically react to a conversation and follow spoken orders. Pick an action (punch, slap, hug, kneel and beg, flee, flip off, square up, nothing...) from what the player said and what the NPC replied, with TypeSafe's Jev returning a typed choice and a probability in ~0.1 s, per-action thresholds, a repeat cooldown, orders ("get on your knees") that override mood and make the LLM rewrite the line, and mapping action names onto the game's animations with checks at load. Use when NPCs should act out what they say, obey commands, or when actions picked by an LLM are too random, too timid or too frequent.
---

# NPC actions

Two questions per exchange, both asked to TypeSafe's **Jev** (a typed-judgment model: it returns one choice from
a fixed set, with a probability, in ~0.1 s; key `TYPESAFE_API_KEY`, `https://docs.typesafe.ai`):

1. **Before the reply (in parallel with the LLM): is the player giving an order?** "Take your shirt off", "get on
   your knees", "give me a hug", "run". If Jev picks an action with ≥ 0.6 probability, it's an order: the NPC
   does it, and the LLM writes the line again knowing "you're <doing it> right now", so they grumble or joke
   while they comply instead of refusing. Costs ~0.1 s more, only then.
2. **After the reply (in parallel with the voice): what does their mood call for?** From the player's words
   and the NPC's reply, pick the action, or none.

## The catalog (`examples/gta-v/mod/ActionCatalog.cs`)
Each action has a name, a description Jev reads, and the probability Jev must reach before it happens:

| Action | Min. probability | Notes |
|---|---|---|
| none | - | just talk: most exchanges |
| slap, punch, facepalm, square_up, threaten_gun, hug, kiss | 0.45 | |
| headbutt, shirt_off (men), pants_off (to underwear), bend_over, kneel_beg | 0.5 | |
| shoot, run_away | 0.6 | need real certainty: they end the conversation |
| middle_finger | 0.7 | Jev picks it a lot, so it needs more |

- **Thresholds per action**, not one global cut-off: tune the ones that fire too often upward.
- **Cooldown:** the same NPC doesn't repeat an action within 60 s unless ordered to.
- **Descriptions are the prompt.** Write when it applies ("only when the reply clearly means killing them right
  now"), not just what it is.
- Gender rules (men only) and content rules (underwear stays on) live in code, not in the prompt.

## Mapping to the game (`NpcActions.cs`)
- Each name maps to candidates: animation dictionary + clip, a task (flee, fight), or a clothing swap. Check
  every candidate exists when the mod loads and log what was found (`action anims:`); fall back to the next.
- Play the action while the line is spoken, aimed at the player (or at whoever the scene says).
- A director or scripted scene can pin an action (`do: kneel_beg`) to skip the picker.

## Test it
`npcvoice-check actions [jev|llm]` (GTA example) runs 25 exchanges through the picker, 10 of them orders, and
checks each pick (25/25 with Jev, 24/25 with the open LLM). In game, dev mode's PgUp/PgDn pick an action and End makes the target do it, to check the
animation without a conversation.

## Without Jev: the open LLM picks
The dialogue LLM can ask the same question (GTA example: `[Actions] Provider = llm`, `LlmActionPicker.cs`). Don't
ask it for a confidence number; those aren't calibrated. Instead:
1. Letter the options (`A. none: ...`, `B. punch: ...`) and ask for one letter, at temperature 0, thinking off.
2. Request `logprobs` with `top_logprobs: 20`, and take each option's probability from its letter's probability
   as the first token, normalized over the valid letters. The per-action thresholds then mean what they mean
   with Jev.
3. If the server returns no logprobs, take the letter it wrote as certain (thresholds stop mattering, so say so).

Measured on `npcvoice-check actions` with Qwen3.8-27B on Cerebras: 24/25 (Jev: 25/25), 0.16-1.55 s per pick
(Jev: 0.07-0.28 s); two runs at temperature 0 gave the same picks. The miss split "I'll put a bullet in your skull" between shoot (45%) and
threaten_gun, so neither reached its threshold. Whatever picks, keep the shape: a fixed set, a probability,
per-action thresholds, orders checked first.
