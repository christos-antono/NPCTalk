using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NAudio.Utils;
using NAudio.Wave;

namespace NpcVoice
{
    /// <summary>Push-to-talk recording: 16 kHz mono 16-bit, which is all speech-to-text needs.</summary>
    internal sealed class MicRecorder : IDisposable
    {
        private WaveInEvent _in;
        private MemoryStream _pcm;

        public bool IsRecording => _in != null;

        /// <summary>Peak of the last 50 ms, 0..1, for the listening meter.</summary>
        public volatile float Level;

        /// <summary>Loudest sample of the whole recording, 0..1. Near zero means the mic is muted or dead.</summary>
        public volatile float Peak;

        public void Start()
        {
            if (_in != null) return;
            var pcm = _pcm = new MemoryStream();
            Level = Peak = 0f;
            _in = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            _in.DataAvailable += (s, e) =>
            {
                lock (pcm) pcm.Write(e.Buffer, 0, e.BytesRecorded);
                int peak = 0;
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(e.Buffer, i)));
                Level = peak / 32768f;
                if (Level > Peak) Peak = Level;
            };
            _in.StartRecording();
        }

        /// <summary>The Windows default recording device, if it is muted (laptop mic-mute keys do this).</summary>
        public static string MutedDefaultMic()
        {
            try
            {
                using (var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator())
                {
                    var mic = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
                    return mic.AudioEndpointVolume.Mute ? mic.FriendlyName : null;
                }
            }
            catch (Exception) { return null; } // no capture device, or Core Audio unavailable
        }

        /// <summary>Stops and returns the recording as a WAV file in memory.</summary>
        public byte[] Stop()
        {
            if (_in == null) return null;
            _in.StopRecording();
            _in.Dispose();
            _in = null;

            byte[] pcm;
            lock (_pcm) pcm = _pcm.ToArray();
            var wav = new MemoryStream();
            using (var w = new WaveFileWriter(new IgnoreDisposeStream(wav), new WaveFormat(16000, 16, 1)))
                w.Write(pcm, 0, pcm.Length);
            return wav.ToArray();
        }

        public static double Seconds(byte[] wav) => wav == null ? 0 : Math.Max(0, wav.Length - 44) / 32000.0;

        public void Dispose()
        {
            _in?.Dispose();
            _in = null;
        }
    }

    /// <summary>What the player said, from a 16 kHz mono WAV.</summary>
    internal interface ISpeechToText
    {
        string Name { get; }
        bool IsConfigured { get; }
        Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);
    }

    /// <summary>fal speech-to-text; the clip goes inline as a data URI so there's no upload round trip.</summary>
    internal sealed class FalSpeechToText : ISpeechToText
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _url, _apiKey;

        public FalSpeechToText(string endpoint, string apiKey)
        {
            _url = "https://fal.run/" + endpoint.Trim('/');
            _apiKey = apiKey;
            Name = endpoint.Trim('/');
        }

        public string Name { get; }

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

        public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var req = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(serializer.Serialize(new Dictionary<string, object>
                {
                    ["audio_url"] = "data:audio/wav;base64," + Convert.ToBase64String(wav),
                }), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Key " + _apiKey);

            using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
            {
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new IOException($"speech-to-text {(int)resp.StatusCode}: {(json.Length > 200 ? json.Substring(0, 200) : json)}");

                var result = (Dictionary<string, object>)serializer.DeserializeObject(json);
                // speech-to-text/turbo returns "output"; Whisper-style endpoints return "text".
                object text = result.ContainsKey("output") ? result["output"] : result.ContainsKey("text") ? result["text"] : null;
                return ((text as string) ?? "").Trim();
            }
        }
    }

    /// <summary>
    /// Any OpenAI-compatible transcription server: POST {base}/audio/transcriptions, multipart file + model,
    /// returns {"text"}. Runs open models on your own GPU or PC: NVIDIA Nemotron ASR (server/asr.py),
    /// Whisper or Parakeet (speaches, vLLM, and others). No key needed for a server on this PC or network.
    /// </summary>
    internal sealed class OpenAiSpeechToText : ISpeechToText
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _url, _apiKey, _model, _language;

        public OpenAiSpeechToText(string baseUrl, string apiKey, string model, string language)
        {
            _url = baseUrl.TrimEnd('/') + "/audio/transcriptions";
            _apiKey = apiKey;
            _model = model;
            _language = language;
        }

        public string Name => _model;

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey) || Net.IsPlainHttp(_url);

        public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "speech.wav");
            form.Add(new StringContent(_model), "model");
            if (!string.IsNullOrEmpty(_language)) form.Add(new StringContent(_language), "language");
            form.Add(new StringContent("json"), "response_format");
            var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = form };
            if (!string.IsNullOrEmpty(_apiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

            using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
            {
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new IOException($"speech-to-text {(int)resp.StatusCode}: {Net.Truncate(json, 200)}");
                var result = (Dictionary<string, object>)Net.Json().DeserializeObject(json);
                return result.TryGetValue("text", out var text) ? ((text as string) ?? "").Trim() : "";
            }
        }
    }
}
