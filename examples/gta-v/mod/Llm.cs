using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NpcVoice
{
    internal sealed class ChatMessage
    {
        public string Role;
        public string Content;
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    /// <summary>
    /// OpenAI-style chat completions; defaults to Cerebras for sub-second dialogue. Any OpenAI-compatible
    /// server works, including open models on your own machine (vLLM, SGLang, llama.cpp, Ollama), which
    /// need no key.
    /// </summary>
    internal sealed class LlmClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _url, _apiKey, _model, _reasoningEffort, _disableReasoning;
        private readonly int _maxTokens;
        private readonly double _temperature;

        /// <param name="disableReasoning">
        /// How to switch off hidden thinking so the line comes back fast: "true" sends Cerebras's
        /// disable_reasoning, "template" sends chat_template_kwargs {"enable_thinking": false} (vLLM, SGLang and
        /// llama.cpp serving Qwen and other hybrid models), anything else sends nothing.
        /// </param>
        public LlmClient(string baseUrl, string apiKey, string model, int maxTokens, double temperature, string reasoningEffort, string disableReasoning)
        {
            _url = baseUrl.TrimEnd('/') + "/chat/completions";
            _apiKey = apiKey;
            _model = model;
            _maxTokens = maxTokens;
            _temperature = temperature;
            _reasoningEffort = reasoningEffort;
            _disableReasoning = (disableReasoning ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>A model, and a key unless the server is on this PC or network (plain http).</summary>
        public bool IsConfigured => !string.IsNullOrEmpty(_model) && (!string.IsNullOrEmpty(_apiKey) || Net.IsPlainHttp(_url));

        public string Model => _model;

        public string Url => _url;

        /// <summary>
        /// Cerebras usually answers in ~0.3 s but occasionally queues for 5+ s; a fresh request
        /// after 2.5 s usually lands on a free slot, so retry once instead of waiting it out.
        /// When it's busy it answers 429 "queue_exceeded" straight away; those are retried after a
        /// short pause too.
        /// </summary>
        public async Task<string> CompleteAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
        {
            var list = messages.ToList();
            int[] busyPausesMs = { 300, 800, 1500 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fast = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        fast.CancelAfter(TimeSpan.FromSeconds(2.5));
                        try { return Text(await CompleteOnceAsync(list, null, fast.Token).ConfigureAwait(false)); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    }
                    return Text(await CompleteOnceAsync(list, null, ct).ConfigureAwait(false));
                }
                catch (LlmBusyException) when (attempt < busyPausesMs.Length)
                {
                    await Task.Delay(busyPausesMs[attempt], ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// The first token of the answer with the probabilities of the likeliest first tokens (OpenAI
        /// logprobs / top_logprobs), at temperature 0. Servers without logprobs return just the token.
        /// </summary>
        public async Task<(string Text, Dictionary<string, double> FirstToken)> FirstTokenAsync(IEnumerable<ChatMessage> messages, int topLogprobs, CancellationToken ct)
        {
            var extra = new Dictionary<string, object>
            {
                ["max_completion_tokens"] = 3, ["temperature"] = 0.0, ["logprobs"] = true, ["top_logprobs"] = topLogprobs,
            };
            var list = messages.ToList();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var choice = await CompleteOnceAsync(list, extra, ct).ConfigureAwait(false);
                    var probs = new Dictionary<string, double>();
                    if (choice.TryGetValue("logprobs", out var lp) && lp is Dictionary<string, object> logprobs
                        && logprobs.TryGetValue("content", out var c) && c is object[] tokens && tokens.Length > 0
                        && ((Dictionary<string, object>)tokens[0]).TryGetValue("top_logprobs", out var top) && top is object[] alternatives)
                    {
                        foreach (Dictionary<string, object> t in alternatives)
                        {
                            string token = (t["token"] as string) ?? "";
                            probs[token] = (probs.TryGetValue(token, out double p) ? p : 0) + Math.Exp(Convert.ToDouble(t["logprob"]));
                        }
                    }
                    return (Text(choice), probs);
                }
                catch (LlmBusyException) when (attempt < 2)
                {
                    await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
        }

        private sealed class LlmBusyException : IOException
        {
            public LlmBusyException(string message) : base(message) { }
        }

        /// <summary>The message text, without any thinking a model left in it (it must never be spoken).</summary>
        private static string Text(Dictionary<string, object> choice)
        {
            var message = (Dictionary<string, object>)choice["message"];
            // Reasoning models that run out of tokens return only "reasoning" and no "content".
            string s = message.TryGetValue("content", out var content) ? (content as string) ?? "" : "";
            s = Regex.Replace(s, @"<think>[\s\S]*?</think>", "");
            if (s.Contains("<think>")) s = ""; // still thinking when the tokens ran out
            return s.Trim();
        }

        private async Task<Dictionary<string, object>> CompleteOnceAsync(List<ChatMessage> messages, Dictionary<string, object> extra, CancellationToken ct)
        {
            var msgs = new List<Dictionary<string, string>>();
            foreach (var m in messages) msgs.Add(new Dictionary<string, string> { ["role"] = m.Role, ["content"] = m.Content });

            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = msgs,
                ["max_completion_tokens"] = _maxTokens,
                ["temperature"] = _temperature,
            };
            if (!string.IsNullOrEmpty(_reasoningEffort)) body["reasoning_effort"] = _reasoningEffort;
            if (_disableReasoning == "true") body["disable_reasoning"] = true; // Cerebras: answer straight away, no hidden thinking
            else if (_disableReasoning == "template") body["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false };
            if (extra != null) foreach (var kv in extra) body[kv.Key] = kv.Value;

            var serializer = new JavaScriptSerializer();
            var req = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(serializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_apiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

            using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
            {
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string error = $"LLM {(int)resp.StatusCode}: {(json.Length > 200 ? json.Substring(0, 200) : json)}";
                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode == 503) throw new LlmBusyException(error);
                    throw new IOException(error);
                }

                var result = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (result == null || !result.TryGetValue("choices", out var choices) || !(choices is object[] list) || list.Length == 0)
                    throw new IOException("LLM returned no choices");
                return (Dictionary<string, object>)list[0];
            }
        }
    }
}
