using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NpcVoice
{
    /// <summary>
    /// The action question answered by an open LLM (the [Llm] model, e.g. Qwen) instead of Jev. The options
    /// are lettered and the model answers with one letter at temperature 0; the probability of each option
    /// is the probability the model gave its letter as the first token (OpenAI logprobs), so the per-action
    /// thresholds in ActionCatalog mean the same thing as with Jev. A server without logprobs gives only the
    /// letter, which then counts as certain.
    /// </summary>
    internal sealed class LlmActionPicker : IActionPicker
    {
        private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private readonly LlmClient _llm;

        public LlmActionPicker(LlmClient llm) { _llm = llm; }

        public string Name => "llm " + _llm.Model;

        public bool IsConfigured => _llm.IsConfigured;

        public async Task<JevChoice> ChooseAsync(object state, string instructions, IDictionary<string, string> criteria, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var names = criteria.Keys.Take(Letters.Length).ToList();
            string options = string.Join("\n", names.Select((n, i) => $"{Letters[i]}. {n}: {criteria[n]}"));
            var prompt = new List<ChatMessage>
            {
                new ChatMessage("system", "You judge one moment of a video game scene. Answer with the single capital letter of the option " +
                                          "that fits best, and nothing else."),
                new ChatMessage("user", $"Scene (JSON): {Net.Json().Serialize(state)}\n\nQuestion: {instructions}\n\nOptions:\n{options}\n\n" +
                                        "Answer with one letter."),
            };
            var (text, firstToken) = await _llm.FirstTokenAsync(prompt, 20, ct).ConfigureAwait(false);

            var probs = new double[names.Count];
            foreach (var kv in firstToken)
            {
                string t = kv.Key.Trim().TrimEnd('.', ')', ':');
                int i = t.Length == 1 ? Letters.IndexOf(t[0]) : -1;
                if (i >= 0 && i < names.Count) probs[i] += kv.Value;
            }
            double mass = probs.Sum();
            int best;
            if (mass > 0) best = Array.IndexOf(probs, probs.Max());
            else
            {
                // No logprobs from this server: take the letter it wrote, as certain.
                string t = text.Trim();
                best = t.Length > 0 ? Letters.IndexOf(char.ToUpperInvariant(t[0])) : -1;
                if (best < 0 || best >= names.Count) best = names.IndexOf("none");
                if (best < 0) best = 0;
                probs[best] = mass = 1;
            }
            return new JevChoice
            {
                Choice = names[best],
                Probability = probs[best] / mass,
                Confidence = Math.Min(1, mass), // how much of the model's first token went to a valid letter
                Seconds = sw.Elapsed.TotalSeconds,
            };
        }
    }
}
