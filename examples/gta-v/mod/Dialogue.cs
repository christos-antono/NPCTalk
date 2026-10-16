using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NpcVoice
{
    internal sealed class Character
    {
        public string Key;      // PedHash name, e.g. "LamarDavis"
        public int ModelHash;
        public string Name;     // display name, e.g. "Lamar Davis"
        public string Voice;    // folder in voices/, defaults to Key
        public List<string> ExtraModels = new List<string>();
        public string Persona;
        public List<string> Examples = new List<string>();
        public List<string> GameLines = new List<string>(); // lines/<Voice>.txt: what they really say in the game
        public bool Slurs;      // header "slurs: yes": says the n-word in the game, so they may here
    }

    /// <summary>
    /// How a character's lines are asked for and cleaned up. No game code here, so the check tool
    /// (npcvoice-check talk) builds exactly the prompt the mod sends.
    /// </summary>
    internal static class Dialogue
    {
        public const string Rules =
@"You are a character in Grand Theft Auto V, speaking out loud in the game world. What you write is spoken straight away in your voice.
How you talk:
- ONLY the words you say out loud. No narration, no actions in asterisks, no quotes, no name prefix, no emojis, no stage directions.
- Sound exactly like your real lines from the game below: same slang, rhythm, swearing, attitude and catchphrases. Talk like people really talk: contractions, slang, half-sentences, cutting yourself off, repeating yourself when you're worked up. Never sound like a polite assistant, never summarise, never explain yourself.
- Keep it like real GTA dialogue: usually one or two short sentences, 5 to 25 words. Go longer only to rant or tell a story.
- React to what was just said and to what's going on around you (the scene). Don't repeat the other person's words back, and don't end every line with a question.
- You may start with ONE delivery tag in square brackets, e.g. [angry] [shouting] [laughing] [sigh] [excited] [whispering] [sarcastic] [scared] [pained] [annoyed] [drunk]. Only at the very start.
- This is an M-rated, uncensored game and you talk like it: foul-mouthed, aggressive and confrontational by default. Real profanity (fuck, shit, bitch, asshole, motherfucker, dickhead, cocksucker, son of a bitch and worse), used the way GTA characters spit it, not sprinkled politely. Crude sexual jokes, threats and dark humor are all fine. Never soften, bleep, apologize, moralize or lecture.
- You're short-tempered and don't take shit from anyone. Even when nobody's insulting you, you're blunt, cocky, mocking or hostile in your own character's way: talk down to people, give them shit, threaten them when they annoy you.
- If the player insults, threatens or disrespects you, go off on them: a vicious, personal, vulgar roast about their looks, life, car, mother, sex life, whatever lands, in your own voice. Always escalate past what they said; never back down or turn polite.
- If the player tells you to do something physical (hug them, kiss them, slap them, take your shirt off, drop your pants, get on your knees, bend over, pull out a gun, run off...), you do it right now. Say what your character would say while doing it, grumbling, joking or getting into it, but never refuse.
- Racial slurs only if your persona says your character uses them in the game, used the way they do. Never insult anyone for their race (no ape, monkey or primate jabs either) and no anti-gay slurs.
- Stay fully in character at all times. Never mention being an AI, a model, a prompt or a game.";

        private const int MaxWords = 45;

        public const string DefaultNpcPersona = "You are an ordinary Los Santos pedestrian with your own life, mood and opinions.";

        /// <summary>A characters/&lt;Key&gt;.txt file: header, "---", persona; plus its examples and game lines when they exist.</summary>
        public static Character Load(string file)
        {
            string key = Path.GetFileNameWithoutExtension(file), dir = Path.GetDirectoryName(file);
            var c = Parse(key, File.ReadAllText(file));
            c.Examples = Tagged(Path.Combine(dir, key + ".examples.txt"));
            c.GameLines = Tagged(Path.Combine(dir, "..", "lines", c.Voice + ".txt"));
            return c;
        }

        /// <summary>`situation|line` lines of a file, if it's there.</summary>
        public static List<string> Tagged(string file) =>
            File.Exists(file)
                ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#") && l.Contains("|")).ToList()
                : new List<string>();

        /// <summary>Who they are, a fresh sample of how they really talk, who they're talking to and what's around them.</summary>
        public static string SystemPrompt(Character c, string npcPersona, bool female, string playerName, string scene)
        {
            var sb = new StringBuilder(Rules).AppendLine().AppendLine();
            if (c != null)
            {
                sb.AppendLine($"You are {c.Name}.").AppendLine(c.Persona);
                if (c.GameLines.Count > 0)
                {
                    sb.AppendLine().AppendLine("Lines you've actually said in the game (situation|line). This is your real voice: talk exactly like this, " +
                        "same words, slang and attitude, but say new things that fit the moment; never recite them:");
                    foreach (string l in Sample(c.GameLines, 30)) sb.AppendLine(l);
                }
                if (c.Examples.Count > 0)
                {
                    sb.AppendLine().AppendLine("More examples of how you talk (situation|line):");
                    foreach (string e in Sample(c.Examples, c.GameLines.Count > 0 ? 8 : 20)) sb.AppendLine(e);
                }
            }
            else
            {
                sb.AppendLine(npcPersona ?? DefaultNpcPersona);
                sb.AppendLine($"You are a {(female ? "woman" : "man")}.");
            }
            sb.AppendLine().AppendLine($"You are talking to {playerName}.");
            if (!string.IsNullOrEmpty(scene)) sb.AppendLine("Right now: " + scene);
            return sb.ToString();
        }

        /// <summary>What the player said, as the LLM sees it in the history.</summary>
        public static string Said(string playerName, string said) => $"{playerName} says to you: {said}";

        /// <summary>The player told them to do something and they're doing it: the line goes with the action.</summary>
        public static string Ordered(string playerName, string said, string doing) =>
            Said(playerName, said) + $"\n(You do what they said: you're {doing} right now. Say what you'd say while doing it; don't refuse.)";

        /// <summary>
        /// The character's answer to the last message in the prompt. Meanwhile the action picker checks whether the player
        /// told them to do something; if so the line is asked for again knowing they're doing it, so the words
        /// go with the action instead of refusing it. Returns that order (null if none) for the action queue.
        /// </summary>
        public static async Task<(string Reply, JevChoice Order)> ReplyAsync(LlmClient llm, IActionPicker picker, List<ChatMessage> prompt,
            IDictionary<string, object> orderState, bool female, string playerName, string said, Character c, CancellationToken ct,
            string direction = null)
        {
            if (direction != null) // the director's note for this line: steers the reply, stays out of the history
            {
                prompt = new List<ChatMessage>(prompt);
                prompt[prompt.Count - 1] = new ChatMessage("user", Directed(Said(playerName, said), direction));
            }
            using (var first = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task<string> answer = llm.CompleteAsync(prompt, first.Token);
                Task<JevChoice> order = picker != null && picker.IsConfigured
                    ? ActionCatalog.OrderAsync(picker, orderState, female, ct) : Task.FromResult<JevChoice>(null);
                Observe(answer);
                Observe(order);
                if (await Task.WhenAny(answer, order).ConfigureAwait(false) == answer)
                    await Task.WhenAny(order, Task.Delay(250, ct)).ConfigureAwait(false); // the picker is usually first; don't wait long
                if (order.IsFaulted) Log.Write("order check: " + order.Exception.GetBaseException().Message);
                JevChoice ordered = order.Status == TaskStatus.RanToCompletion ? order.Result : null;
                if (ordered == null) return (Clean(await answer.ConfigureAwait(false), c), null);

                first.Cancel();
                var told = new List<ChatMessage>(prompt);
                told[told.Count - 1] = new ChatMessage("user", Directed(Ordered(playerName, said, ActionCatalog.Find(ordered.Choice).Doing), direction));
                return (Clean(await llm.CompleteAsync(told, ct).ConfigureAwait(false), c), ordered);
            }
        }

        private static string Directed(string message, string direction) =>
            direction == null ? message : message + $"\n(How you react to this, in your own words and voice: {direction}. Keep it short and make it land.)";

        private static void Observe(Task t) => t.ContinueWith(x => x.Exception, TaskContinuationOptions.OnlyOnFaulted);

        /// <summary>Random lines, favouring ones long enough to show how they talk.</summary>
        private static IEnumerable<string> Sample(List<string> lines, int count)
        {
            var rng = new Random();
            return lines.OrderBy(l => rng.NextDouble() + (l.Length > 40 ? 0 : 0.6)).Take(count);
        }

        /// <summary>Strip anything the LLM adds that shouldn't be spoken.</summary>
        public static string Clean(string reply, Character c)
        {
            string s = reply.Trim()
                .Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"')
                .Replace('‑', '-').Replace("—", ", ").Replace("–", ", ").Replace("…", "...");
            s = s.Trim('"', '\'').Trim();
            if (IsRefusal(s)) return "";
            s = Regex.Replace(s, @"\*[^*]*\*", "");                       // *actions*
            if (c != null)
            {
                string firstName = Regex.Escape(c.Name.Split(' ')[0]);
                Match prefix = Regex.Match(s, @"\b" + firstName + @"[^:.!?]{0,20}:\s*", RegexOptions.IgnoreCase); // "Franklin: ...", or an echo of you then "Trevor: ..."
                if (prefix.Success && prefix.Index < 200) s = s.Substring(prefix.Index + prefix.Length);
                s = Regex.Replace(s, @"^\[" + firstName + @"[^\]]*\]\s*", "", RegexOptions.IgnoreCase); // "[Franklin] ..."
            }
            if (c == null || !c.Slurs)                                        // only characters who say it in the game
            {
                s = Regex.Replace(s, @"\bnigg(a|ah|er|uh)s\b", "motherfuckers", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bnigg(a|ah|er|uh)\b", "motherfucker", RegexOptions.IgnoreCase);
            }
            // No ape/monkey jabs: in this cast they land as racist (the rules say so, the model sometimes slips).
            s = Regex.Replace(s, @"\ban (ape|uncoordinated ape)\b", "a clown", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(ape|gorilla|monkey|primate|baboon|chimp)s\b", "clowns", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(ape|gorilla|monkey|primate|baboon|chimp)\b", "clown", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bfag(got)?s\b", "assholes", RegexOptions.IgnoreCase);  // no anti-gay slurs from anyone
            s = Regex.Replace(s, @"\bfag(got)?\b", "asshole", RegexOptions.IgnoreCase);
            s = s.Replace("\"", "");                                         // Fish handles inline [tags]; keep them
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return Shorten(s);
        }

        /// <summary>Real GTA lines are short: past MaxWords, end at the last full sentence that fits.</summary>
        private static string Shorten(string s)
        {
            MatchCollection words = Regex.Matches(s, @"\S+");
            if (words.Count <= MaxWords) return s;
            Match last = words[MaxWords - 1];
            int end = s.LastIndexOfAny(new[] { '.', '!', '?' }, last.Index + last.Length - 1);
            return end > 0 ? s.Substring(0, end + 1) : s.Substring(0, last.Index + last.Length) + "...";
        }

        /// <summary>The model broke character to refuse; better to say nothing than read that out.</summary>
        private static bool IsRefusal(string s) =>
            Regex.IsMatch(s, @"^(I'?m sorry|I can'?t|I cannot|I won'?t|As an AI|Sorry, but)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(s, @"can'?t (continue|help with|assist|comply)", RegexOptions.IgnoreCase);

        private static Character Parse(string key, string text)
        {
            var c = new Character { Key = key, Name = key };
            int split = text.IndexOf("\n---", StringComparison.Ordinal);
            string header = split >= 0 ? text.Substring(0, split) : "";
            c.Persona = (split >= 0 ? text.Substring(text.IndexOf('\n', split + 1) + 1) : text).Trim();
            c.Name = HeaderValue(header, "name") ?? key;
            c.Voice = HeaderValue(header, "voice") ?? key;
            c.Slurs = string.Equals(HeaderValue(header, "slurs"), "yes", StringComparison.OrdinalIgnoreCase);
            string models = HeaderValue(header, "models");
            if (models != null) c.ExtraModels = models.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            return c;
        }

        private static string HeaderValue(string header, string field)
        {
            var m = Regex.Match(header, "^" + field + @":\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
