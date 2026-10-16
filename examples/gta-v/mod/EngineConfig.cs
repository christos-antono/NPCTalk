using System;
using System.Globalization;

namespace NpcVoice
{
    /// <summary>
    /// Builds the LLM, speech-to-text and action picker from NpcVoice.ini, the same way in the game and in the
    /// check tool. Every part can run on open models (README, "Open models"): the LLM and speech-to-text on any
    /// OpenAI-compatible server, the actions on the LLM.
    /// </summary>
    internal static class EngineConfig
    {
        /// <summary>One ini value, (section, key, default); an empty value counts as missing.</summary>
        public delegate string Setting(string section, string key, string fallback);

        /// <summary>An API key: the environment variable named in the ini (process, then user), else the ini's ApiKey.</summary>
        public static string Key(string envName, string fallback)
        {
            string fromEnv = string.IsNullOrEmpty(envName) ? null : Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(fromEnv) && !string.IsNullOrEmpty(envName))
                fromEnv = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.User);
            return string.IsNullOrEmpty(fromEnv) ? fallback : fromEnv.Trim();
        }

        /// <summary>[Llm]: the characters' lines, from any OpenAI-compatible chat server (Cerebras by default).</summary>
        public static LlmClient Llm(Setting ini) => new LlmClient(
            Net.HttpsUrl(ini("Llm", "BaseUrl", "api.cerebras.ai/v1"), "https://api.cerebras.ai/v1"),
            Key(ini("Llm", "ApiKeyEnv", "CEREBRAS_API_KEY"), ini("Llm", "ApiKey", "")),
            ini("Llm", "Model", "qwen-3.8-27b"),
            int.TryParse(ini("Llm", "MaxTokens", "160"), out int maxTokens) ? maxTokens : 160,
            double.TryParse(ini("Llm", "Temperature", "0.9"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) ? t : 0.9,
            ini("Llm", "ReasoningEffort", ""),
            ini("Llm", "DisableReasoning", "true"));

        /// <summary>[Stt] Provider: fal (an endpoint on fal, FAL_KEY) or openai (any OpenAI-compatible transcription server).</summary>
        public static ISpeechToText SpeechToText(Setting ini, string falKey)
        {
            if (ini("Stt", "Provider", "fal").Trim().Equals("openai", StringComparison.OrdinalIgnoreCase))
                return new OpenAiSpeechToText(
                    Net.HttpsUrl(ini("Stt", "BaseUrl", "127.0.0.1:8000/v1"), "http://127.0.0.1:8000/v1"),
                    Key(ini("Stt", "ApiKeyEnv", ""), ini("Stt", "ApiKey", "")),
                    ini("Stt", "Model", "nvidia/nemotron-3.5-asr-streaming-0.6b"),
                    ini("Stt", "Language", "en"));
            return new FalSpeechToText(ini("Stt", "Endpoint", "nvidia/nemotron-asr-multilingual/asr"), falKey);
        }

        /// <summary>[Actions] Provider: jev (TypeSafe, TYPESAFE_API_KEY) or llm (the [Llm] model picks).</summary>
        public static IActionPicker ActionPicker(Setting ini, LlmClient llm)
        {
            if (ini("Actions", "Provider", "jev").Trim().Equals("llm", StringComparison.OrdinalIgnoreCase))
                return new LlmActionPicker(llm);
            return new JevClient(
                Net.HttpsUrl(ini("Actions", "BaseUrl", "api.typesafe.ai"), "https://api.typesafe.ai"),
                Key(ini("Actions", "ApiKeyEnv", "TYPESAFE_API_KEY"), ini("Actions", "ApiKey", "")),
                ini("Actions", "Model", "jev-latest"));
        }
    }
}
