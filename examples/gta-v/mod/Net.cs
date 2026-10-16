using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NpcVoice
{
    /// <summary>
    /// Process-wide HTTP setup. GTA5.exe has no .NET entry assembly to opt into modern TLS, so
    /// HttpClient falls back to SSL3/TLS 1.0 and every HTTPS call fails with "An error occurred
    /// while sending the request". Call Init() before the first request.
    /// </summary>
    internal static class Net
    {
        public static void Init()
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; } // | Tls13
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 16;
        }

        public static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>
        /// A base URL from NpcVoice.ini. ScriptHookVDotNet's ini reader cuts values at "//" (it treats
        /// it as a comment), so "https://api.cerebras.ai/v1" arrives as "https". Addresses are written
        /// without the scheme and get https:// here, or http:// for a server on this PC or the local
        /// network (127.0.0.1:11434/v1, 192.168.1.20:8000/v1), which rarely has TLS; a value that was cut
        /// off falls back to the default.
        /// </summary>
        public static string HttpsUrl(string value, string fallback)
        {
            string v = (value ?? "").Trim().TrimEnd('/');
            if (v.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(v, "^https?:?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return fallback;
            if (!v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                v = (IsLocal(v) ? "http://" : "https://") + v;
            return Uri.TryCreate(v, UriKind.Absolute, out _) ? v : fallback;
        }

        /// <summary>This PC or a private network address: localhost, 127.x, [::1], 10.x, 172.16-31.x, 192.168.x, *.local.</summary>
        public static bool IsLocal(string hostAndPath) =>
            System.Text.RegularExpressions.Regex.IsMatch(hostAndPath,
                @"^(localhost|127\.\d+\.\d+\.\d+|\[::1\]|10\.\d+\.\d+\.\d+|192\.168\.\d+\.\d+|172\.(1[6-9]|2\d|3[01])\.\d+\.\d+|[\w-]+\.local)(:\d+)?(/|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>A plain-http URL (a server on this PC or network), which needs no API key.</summary>
        public static bool IsPlainHttp(string url) => url != null && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        public static string Truncate(string s, int max = 300) => s == null ? "" : s.Length > max ? s.Substring(0, max) : s;

        private static readonly HttpClient StreamHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>POST JSON with a bearer token and read the response body as it streams in.</summary>
        public static async Task PostStreamAsync(string url, string bearer, Dictionary<string, object> body,
            Func<HttpResponseMessage, Stream, Task> read, CancellationToken ct, int timeoutSeconds)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(Json().Serialize(body), Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(bearer)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                using (var resp = await StreamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new IOException($"{url} {(int)resp.StatusCode}: {Truncate(await resp.Content.ReadAsStringAsync().ConfigureAwait(false), 200)}");
                    using (Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (cts.Token.Register(s.Dispose)) // a blocked read ends when the stream closes
                        await read(resp, s).ConfigureAwait(false);
                }
            }
        }
    }

    internal sealed class FalException : IOException
    {
        public int Status { get; }
        public string Body { get; }

        public FalException(int status, string endpoint, string body)
            : base($"{endpoint} {status}: {Net.Truncate(body, 200)}")
        {
            Status = status;
            Body = body;
        }
    }

    /// <summary>Synchronous fal model calls (https://fal.run/&lt;endpoint&gt;), result downloads and storage uploads.</summary>
    internal static class FalApi
    {
        // Per-call timeouts come from cancellation tokens; a cold runner can take a minute or more.
        private static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        public static string Key;

        public static async Task<Dictionary<string, object>> RunAsync(string endpoint, Dictionary<string, object> input, CancellationToken ct, int timeoutSeconds = 180)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var req = new HttpRequestMessage(HttpMethod.Post, "https://fal.run/" + endpoint.Trim('/'))
                {
                    Content = new StringContent(Net.Json().Serialize(input), Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("Authorization", "Key " + Key);
                using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new FalException((int)resp.StatusCode, endpoint, body);
                    return (Dictionary<string, object>)Net.Json().DeserializeObject(body);
                }
            }
        }

        /// <summary>
        /// RunAsync, plus an identical second request if the first hasn't answered after
        /// <paramref name="hedgeAfterMs"/>; whichever succeeds first wins and the other is cancelled.
        /// Cuts the occasional slow outlier for a few cents per thousand lines.
        /// </summary>
        public static async Task<Dictionary<string, object>> RunHedgedAsync(string endpoint, Dictionary<string, object> input, int hedgeAfterMs, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var tasks = new List<Task<Dictionary<string, object>>> { RunAsync(endpoint, input, cts.Token) };
                if (await Task.WhenAny(tasks[0], Task.Delay(hedgeAfterMs, cts.Token)).ConfigureAwait(false) != tasks[0])
                    tasks.Add(RunAsync(endpoint, input, cts.Token));

                while (true)
                {
                    var done = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(done);
                    if (done.Status == TaskStatus.RanToCompletion || tasks.Count == 0)
                    {
                        cts.Cancel(); // drop the slower request
                        foreach (var t in tasks) _ = t.ContinueWith(x => x.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        return await done.ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>The url of a File output such as {"audio": {"url": ...}}.</summary>
        public static string FileUrl(Dictionary<string, object> result, string field)
        {
            if (result.TryGetValue(field, out var f) && f is Dictionary<string, object> file && file.TryGetValue("url", out var url))
                return url as string;
            throw new IOException($"fal result has no {field}.url");
        }

        /// <summary>GET a result file and hand its body to <paramref name="read"/> as it streams in.</summary>
        public static async Task DownloadAsync(string url, Func<Stream, Task> read, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using (Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (cts.Token.Register(s.Dispose)) // a blocked read ends when the stream closes
                        await read(s).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Upload a local file to fal storage; returns a URL any fal endpoint can download.</summary>
        public static async Task<string> UploadAsync(string path, string contentType)
        {
            var init = new HttpRequestMessage(HttpMethod.Post, "https://rest.fal.ai/storage/upload/initiate?storage_type=gcs")
            {
                Content = new StringContent(Net.Json().Serialize(new Dictionary<string, string>
                {
                    ["file_name"] = Path.GetFileName(path),
                    ["content_type"] = contentType,
                }), Encoding.UTF8, "application/json"),
            };
            init.Headers.TryAddWithoutValidation("Authorization", "Key " + Key);

            string body;
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            using (var resp = await Http.SendAsync(init, cts.Token).ConfigureAwait(false))
            {
                body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new FalException((int)resp.StatusCode, "storage upload", body);
            }

            var result = (Dictionary<string, object>)Net.Json().DeserializeObject(body);
            var put = new HttpRequestMessage(HttpMethod.Put, (string)result["upload_url"])
            {
                Content = new ByteArrayContent(File.ReadAllBytes(path)),
            };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            using (var resp = await Http.SendAsync(put, cts.Token).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new FalException((int)resp.StatusCode, "storage put", await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            return (string)result["file_url"];
        }
    }
}
