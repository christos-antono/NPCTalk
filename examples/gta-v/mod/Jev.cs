using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NpcVoice
{
    /// <summary>The pick for one Choice question: the option, its probability and the picker's confidence.</summary>
    internal sealed class JevChoice
    {
        public string Choice;
        public double Probability;
        public double Confidence;
        public double Seconds;
        public bool Ordered;     // the player told them to do it (not picked from the mood)
    }

    /// <summary>
    /// TypeSafe's Jev (https://docs.typesafe.ai): typed judgments instead of free text. The mod asks
    /// one Choice question per exchange: which physical action goes with what the NPC just said.
    /// POST https://api.typesafe.ai/v1/systemone, "Authorization: Bearer $TYPESAFE_API_KEY".
    /// </summary>
    /// <summary>Picks one option for a question about a scene: TypeSafe's Jev, or an open LLM (LlmActionPicker).</summary>
    internal interface IActionPicker
    {
        string Name { get; }
        bool IsConfigured { get; }

        /// <param name="state">What is judged (serialized as JSON).</param>
        /// <param name="instructions">The question.</param>
        /// <param name="criteria">Option name -> what it means.</param>
        Task<JevChoice> ChooseAsync(object state, string instructions, IDictionary<string, string> criteria, CancellationToken ct);
    }

    internal sealed class JevClient : IActionPicker
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly string _url, _key, _model;

        public JevClient(string baseUrl, string apiKey, string model)
        {
            _url = baseUrl.TrimEnd('/') + "/v1/systemone";
            _key = apiKey;
            _model = model;
        }

        public string Name => "jev";

        public bool IsConfigured => !string.IsNullOrEmpty(_key);

        /// <param name="state">What Jev judges (serialized as JSON).</param>
        /// <param name="instructions">The question.</param>
        /// <param name="criteria">Option name -> what it means.</param>
        public async Task<JevChoice> ChooseAsync(object state, string instructions, IDictionary<string, string> criteria, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["state"] = state,
                ["questions"] = new Dictionary<string, object>
                {
                    ["action"] = new Dictionary<string, object>
                    {
                        ["type"] = "choice",
                        ["instructions"] = instructions,
                        ["criteria"] = criteria,
                    },
                },
            };
            for (int attempt = 0; ; attempt++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, _url)
                {
                    Content = new StringContent(Net.Json().Serialize(body), Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    int status = (int)resp.StatusCode;
                    if ((status == 429 || status == 529) && attempt < 2)
                    {
                        await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode) throw new IOException($"Jev {status}: {Net.Truncate(json, 200)}");

                    var result = (Dictionary<string, object>)Net.Json().DeserializeObject(json);
                    var answer = (Dictionary<string, object>)((Dictionary<string, object>)result["answers"])["action"];
                    string choice = (string)answer["choice"];
                    var probabilities = (Dictionary<string, object>)answer["probabilities"];
                    return new JevChoice
                    {
                        Choice = choice,
                        Probability = probabilities.TryGetValue(choice, out var p) ? Convert.ToDouble(p) : 0,
                        Confidence = answer.TryGetValue("confidence", out var c) ? Convert.ToDouble(c) : 0,
                        Seconds = sw.Elapsed.TotalSeconds,
                    };
                }
            }
        }
    }
}
