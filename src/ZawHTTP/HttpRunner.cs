using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;

namespace ZawHTTP;

/// <summary>A request after variables, auth and defaults were applied: what goes on the wire.</summary>
internal sealed record PreparedRequest(string Method, Uri Uri, List<HeaderLine> Headers, byte[]? Body, int TimeoutMs, bool FollowRedirects, int MaxRedirects, bool VerifySsl);

/// <summary>A response body kept in memory for a while, so the UI can preview images or save the bytes.</summary>
public sealed record StoredBody(byte[] Bytes, string? ContentType, string FileName);

/// <summary>Executes requests. Redirects are followed by hand so each hop can be shown in the timeline.</summary>
public sealed class HttpRunner : IDisposable
{
    public const long MaxBody = 100L * 1024 * 1024;
    const int MaxTextBytes = 10 * 1024 * 1024;
    const int MaxStoredBodies = 30;
    const int MaxPreview = 64 * 1024;

    public static readonly string Version = typeof(HttpRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
    public static readonly string UserAgent = $"ZawHTTP/{Version}";

    static readonly FileExtensionContentTypeProvider Mime = new();
    static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Language", "Content-Location",
        "Content-MD5", "Content-Range", "Content-Disposition", "Expires", "Last-Modified", "Allow",
    };

    readonly CookieContainer jar = new();
    readonly HttpClient verified, unverified;
    readonly ConcurrentDictionary<string, CancellationTokenSource> runs = new();
    readonly ConcurrentDictionary<string, StoredBody> bodies = new();
    readonly ConcurrentQueue<string> bodyOrder = new();

    public HttpRunner()
    {
        verified = CreateClient(verifySsl: true);
        unverified = CreateClient(verifySsl: false);
    }

    HttpClient CreateClient(bool verifySsl)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = jar,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        if (!verifySsl) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        verified.Dispose();
        unverified.Dispose();
    }

    public bool Cancel(string runId)
    {
        if (!runs.TryGetValue(runId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public StoredBody? Body(string id) => bodies.TryGetValue(id, out var b) ? b : null;

    // ------------------------------------------------------------------ preparing

    /// <summary>Resolves variables and builds the method, URL, headers and body bytes of a request.</summary>
    internal static async Task<PreparedRequest> PrepareAsync(HttpRequestModel m, Variables vars, CancellationToken ct = default)
    {
        var method = vars.Apply(m.Method).Trim().ToUpperInvariant();
        if (method.Length == 0) method = "GET";
        if (!method.All(c => c > ' ' && c < 127 && !"()<>@,;:\\\"/[]?={}".Contains(c)))
            throw new ApiException($"\"{method}\" is not a valid HTTP method.");

        var url = vars.Apply(m.Url).Trim();
        if (url.Length == 0) throw new ApiException("Enter a URL to send the request to.");
        if (!url.Contains("://")) url = "http://" + url;

        var auth = m.Auth ?? new RequestAuth();
        var headers = new List<HeaderLine>();
        foreach (var h in m.Headers ?? [])
        {
            if (!h.Enabled || string.IsNullOrWhiteSpace(h.Key)) continue;
            headers.Add(new HeaderLine(vars.Apply(h.Key).Trim(), vars.Apply(h.Value)));
        }
        bool Has(string name) => headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // Auth adds its header unless the request sets that header itself.
        switch (auth.Type)
        {
            case "basic" when !Has("Authorization"):
                var pair = $"{vars.Apply(auth.Username)}:{vars.Apply(auth.Password)}";
                headers.Add(new HeaderLine("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair))));
                break;
            case "bearer" when !Has("Authorization"):
                headers.Add(new HeaderLine("Authorization", "Bearer " + vars.Apply(auth.Token).Trim()));
                break;
            case "apikey" when !string.IsNullOrWhiteSpace(auth.Key):
                var key = vars.Apply(auth.Key).Trim();
                var value = vars.Apply(auth.Value);
                if (auth.In == "query")
                    url += (url.Contains('?') ? "&" : "?") + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value);
                else if (!Has(key))
                    headers.Add(new HeaderLine(key, value));
                break;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Host.Length == 0)
            throw new ApiException($"\"{url}\" is not a valid http:// or https:// URL.");

        var (body, contentType) = await BuildBodyAsync(m.Body, vars, ct);
        if (body != null && contentType != null)
        {
            // An explicit Content-Type wins, except one for multipart data without the boundary we generated.
            var own = headers.FindIndex(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
            if (own < 0) headers.Add(new HeaderLine("Content-Type", contentType));
            else if (contentType.StartsWith("multipart/", StringComparison.Ordinal) && !headers[own].Value.Contains("boundary=", StringComparison.OrdinalIgnoreCase))
                headers[own] = new HeaderLine(headers[own].Name, contentType);
        }
        if (!Has("User-Agent")) headers.Add(new HeaderLine("User-Agent", UserAgent));
        if (!Has("Accept")) headers.Add(new HeaderLine("Accept", "*/*"));

        var s = m.Settings ?? new RequestSettings();
        return new PreparedRequest(method, uri, headers, body, Math.Max(0, s.TimeoutMs), s.FollowRedirects, Math.Clamp(s.MaxRedirects, 0, 50), s.VerifySsl);
    }

    static async Task<(byte[]?, string?)> BuildBodyAsync(RequestBody? b, Variables vars, CancellationToken ct)
    {
        switch (b?.Mode)
        {
            case "raw":
                var raw = vars.Apply(b.Raw);
                var type = b.RawType switch
                {
                    "json" => "application/json",
                    "xml" => "application/xml",
                    "html" => "text/html",
                    "javascript" => "application/javascript",
                    _ => "text/plain",
                };
                return (Encoding.UTF8.GetBytes(raw), type);

            case "urlencoded":
                var pairs = (b.Urlencoded ?? []).Where(f => f.Enabled && !string.IsNullOrEmpty(f.Key))
                    .Select(f => new KeyValuePair<string, string>(vars.Apply(f.Key), vars.Apply(f.Value)));
                using (var form = new FormUrlEncodedContent(pairs))
                    return (await form.ReadAsByteArrayAsync(ct), "application/x-www-form-urlencoded");

            case "formdata":
                // A boundary without quotes: some servers don't accept the quoted one .NET would send.
                var boundary = "----ZawHTTPBoundary" + Guid.NewGuid().ToString("n");
                using (var mp = new MultipartFormDataContent(boundary))
                {
                    foreach (var f in b.Form ?? [])
                    {
                        if (!f.Enabled || string.IsNullOrEmpty(f.Key)) continue;
                        var name = vars.Apply(f.Key);
                        if (f.Type == "file")
                        {
                            var path = ResolveFile(vars.Apply(f.Value));
                            var part = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
                            part.Headers.ContentType = new MediaTypeHeaderValue(GuessType(path));
                            mp.Add(part, name, Path.GetFileName(path));
                        }
                        else
                        {
                            mp.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(vars.Apply(f.Value))), name);
                        }
                    }
                    return (await mp.ReadAsByteArrayAsync(ct), "multipart/form-data; boundary=" + boundary);
                }

            case "binary":
                var file = ResolveFile(vars.Apply(b.File));
                return (await File.ReadAllBytesAsync(file, ct), GuessType(file));

            default:
                return (null, null);
        }
    }

    static string ResolveFile(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) throw new ApiException("Choose a file to send.");
        if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        if (!File.Exists(path)) throw new ApiException($"File not found: {path}");
        return path;
    }

    static string GuessType(string path) => Mime.TryGetContentType(path, out var t) ? t : "application/octet-stream";

    // ------------------------------------------------------------------ sending

    public async Task<HttpResult> SendAsync(SendRequest r, CancellationToken aborted)
    {
        var vars = new Variables(r.Variables);
        var p = await PrepareAsync(r.Request, vars, aborted);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        using var timeout = new CancellationTokenSource();
        if (p.TimeoutMs > 0) timeout.CancelAfter(p.TimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
        var runId = r.RunId ?? Guid.NewGuid().ToString("n");
        runs[runId] = cts;
        try
        {
            return await ExecuteAsync(p, vars, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cts.IsCancellationRequested)
        {
            throw new ApiException($"The request timed out after {p.TimeoutMs:N0} ms.");
        }
        catch (OperationCanceledException)
        {
            throw new ApiException("The request was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(Describe(ex));
        }
        finally
        {
            runs.TryRemove(runId, out _);
        }
    }

    async Task<HttpResult> ExecuteAsync(PreparedRequest p, Variables vars, CancellationToken ct)
    {
        var client = p.VerifySsl ? verified : unverified;
        var method = p.Method;
        var uri = p.Uri;
        var headers = p.Headers;
        var body = p.Body;
        var hops = new List<RedirectHop>();
        var sw = Stopwatch.StartNew();

        HttpRequestMessage msg;
        HttpResponseMessage resp;
        while (true)
        {
            msg = BuildMessage(method, uri, headers, body);
            resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)resp.StatusCode;
            var location = resp.Headers.Location;
            if (!p.FollowRedirects || status is not (301 or 302 or 303 or 307 or 308) || location == null || hops.Count >= p.MaxRedirects)
                break;

            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            hops.Add(new RedirectHop(status, method, uri.ToString(), next.ToString()));
            // Like browsers: 303 turns everything but HEAD into GET, 301/302 turn POST into GET; the body goes with it.
            if ((status == 303 && method != "HEAD") || (status is 301 or 302 && method == "POST"))
            {
                method = "GET";
                body = null;
                headers = headers.Where(h => !ContentHeaders.Contains(h.Name)).ToList();
            }
            // Credentials never follow a redirect to another host.
            if (!string.Equals(next.Host, uri.Host, StringComparison.OrdinalIgnoreCase) || next.Port != uri.Port)
                headers = headers.Where(h => !h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).ToList();
            resp.Dispose();
            msg.Dispose();
            uri = next;
        }

        using var _resp = resp;
        using var _msg = msg;
        var ttfb = sw.Elapsed.TotalMilliseconds;

        var buffer = new MemoryStream();
        var truncated = false;
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
        {
            var chunk = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > MaxBody)
                {
                    buffer.Write(chunk, 0, (int)(MaxBody - buffer.Length));
                    truncated = true;
                    break;
                }
                buffer.Write(chunk, 0, n);
            }
        }
        var total = sw.Elapsed.TotalMilliseconds;
        var bytes = buffer.ToArray();

        var respHeaders = resp.Headers.Concat(resp.Content.Headers)
            .SelectMany(h => h.Value.Select(v => new HeaderLine(h.Key, v))).ToList();
        var contentType = resp.Content.Headers.ContentType?.ToString();
        var isText = LooksTextual(resp.Content.Headers.ContentType?.MediaType, bytes);
        string? text = null;
        var textTruncated = false;
        if (isText)
        {
            textTruncated = bytes.Length > MaxTextBytes;
            text = Decode(bytes.AsSpan(0, Math.Min(bytes.Length, MaxTextBytes)).ToArray(), resp.Content.Headers.ContentType?.CharSet);
        }

        var id = Guid.NewGuid().ToString("n");
        Store(id, new StoredBody(bytes, contentType, FileNameFor(resp, uri)));

        var cookies = resp.Headers.TryGetValues("Set-Cookie", out var set) ? set.Select(ParseSetCookie).ToList() : [];
        var sent = SentSnapshot(msg, uri, body);
        var version = resp.Version.Major >= 2 ? $"HTTP/{resp.Version.Major}" : $"HTTP/{resp.Version}";

        return new HttpResult(id, (int)resp.StatusCode, resp.ReasonPhrase ?? "", version, uri.ToString(), respHeaders, cookies,
            contentType, bytes.LongLength, truncated, isText, text, textTruncated, Math.Round(ttfb, 1), Math.Round(total, 1),
            hops, sent, [.. vars.Unresolved]);
    }

    static HttpRequestMessage BuildMessage(string method, Uri uri, List<HeaderLine> headers, byte[]? body)
    {
        var msg = new HttpRequestMessage(new HttpMethod(method), uri);
        if (body != null) msg.Content = new ByteArrayContent(body);
        foreach (var h in headers)
        {
            if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue; // always computed from the body
            if (ContentHeaders.Contains(h.Name))
            {
                msg.Content ??= new ByteArrayContent([]);
                msg.Content.Headers.TryAddWithoutValidation(h.Name, h.Value);
            }
            else if (!msg.Headers.TryAddWithoutValidation(h.Name, h.Value))
            {
                throw new ApiException($"The header \"{h.Name}\" is not valid.");
            }
        }
        return msg;
    }

    SentRequest SentSnapshot(HttpRequestMessage msg, Uri uri, byte[]? body)
    {
        var lines = msg.Headers.Concat(msg.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .SelectMany(h => h.Value.Select(v => new HeaderLine(h.Key, v))).ToList();
        var jarCookies = jar.GetCookieHeader(uri);
        if (jarCookies.Length > 0 && !lines.Any(l => l.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            lines.Add(new HeaderLine("Cookie", jarCookies));
        string? preview = null;
        if (body is { Length: > 0 })
        {
            var head = body.AsSpan(0, Math.Min(body.Length, MaxPreview));
            preview = head.Contains((byte)0) ? null : Encoding.UTF8.GetString(head) + (body.Length > MaxPreview ? "\n…" : "");
        }
        return new SentRequest(msg.Method.Method, uri.ToString(), lines, body?.LongLength ?? 0, preview);
    }

    void Store(string id, StoredBody b)
    {
        bodies[id] = b;
        bodyOrder.Enqueue(id);
        while (bodyOrder.Count > MaxStoredBodies && bodyOrder.TryDequeue(out var old)) bodies.TryRemove(old, out _);
    }

    static string FileNameFor(HttpResponseMessage resp, Uri uri)
    {
        var cd = resp.Content.Headers.ContentDisposition;
        var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"');
        if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? "");
        if (string.IsNullOrWhiteSpace(name)) name = "response";
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (!Path.HasExtension(name))
        {
            var mt = resp.Content.Headers.ContentType?.MediaType;
            var ext = mt == null ? null : Mime.Mappings.FirstOrDefault(kv => kv.Value == mt).Key;
            if (mt != null && mt.Contains("json")) ext = ".json";
            name += ext ?? (mt == null ? "" : ".bin");
        }
        return name;
    }

    // ------------------------------------------------------------------ helpers

    internal static bool LooksTextual(string? mediaType, ReadOnlySpan<byte> bytes)
    {
        if (!string.IsNullOrEmpty(mediaType))
        {
            var mt = mediaType.ToLowerInvariant();
            if (mt.StartsWith("text/") || mt.Contains("json") || mt.Contains("xml") || mt.Contains("javascript") || mt.Contains("ecmascript")
                || mt.Contains("x-www-form-urlencoded") || mt.Contains("yaml") || mt.Contains("graphql") || mt.Contains("csv"))
                return true;
            if (mt.StartsWith("image/") || mt.StartsWith("audio/") || mt.StartsWith("video/") || mt.StartsWith("font/")
                || mt is "application/octet-stream" or "application/pdf" or "application/zip" or "application/gzip")
                return false;
        }
        return !bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0);
    }

    internal static string Decode(byte[] bytes, string? charset)
    {
        var enc = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { enc = Encoding.GetEncoding(charset.Trim('"')); } catch (ArgumentException) { /* unknown charset: UTF-8 */ }
        }
        using var reader = new StreamReader(new MemoryStream(bytes), enc, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static CookieInfo ParseSetCookie(string header)
    {
        var parts = header.Split(';');
        var nv = parts[0];
        var eq = nv.IndexOf('=');
        string? domain = null, path = null, expires = null, sameSite = null;
        bool secure = false, httpOnly = false;
        foreach (var raw in parts.Skip(1))
        {
            var a = raw.Trim();
            var i = a.IndexOf('=');
            var k = (i < 0 ? a : a[..i]).Trim().ToLowerInvariant();
            var v = i < 0 ? "" : a[(i + 1)..].Trim();
            switch (k)
            {
                case "domain": domain = v; break;
                case "path": path = v; break;
                case "expires": expires = v; break;
                case "max-age": expires ??= int.TryParse(v, out var secs) ? DateTime.UtcNow.AddSeconds(secs).ToString("R") : v; break;
                case "samesite": sameSite = v; break;
                case "secure": secure = true; break;
                case "httponly": httpOnly = true; break;
            }
        }
        return new CookieInfo(eq < 0 ? nv.Trim() : nv[..eq].Trim(), eq < 0 ? "" : nv[(eq + 1)..].Trim(), domain, path, expires, secure, httpOnly, sameSite);
    }

    static string Describe(HttpRequestException ex)
    {
        var msg = ex.Message;
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
                return $"{msg} {inner.Message} If you trust this server, switch off \"Verify SSL certificate\" in the request's Settings tab.";
            if (!msg.Contains(inner.Message, StringComparison.Ordinal)) msg += " " + inner.Message;
        }
        return msg;
    }

    // ------------------------------------------------------------------ cookie jar

    public List<object> Cookies() =>
        jar.GetAllCookies().Where(c => !c.Expired)
            .OrderBy(c => c.Domain, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => (object)new
            {
                c.Name, c.Value, c.Domain, c.Path, c.Secure, c.HttpOnly,
                Expires = c.Expires == DateTime.MinValue ? null : c.Expires.ToUniversalTime().ToString("R"),
            }).ToList();

    /// <summary>Removes the matching cookies (all of them when every filter is null).</summary>
    public int DeleteCookies(string? domain, string? name, string? path)
    {
        var n = 0;
        foreach (Cookie c in jar.GetAllCookies())
        {
            if ((domain == null || c.Domain == domain) && (name == null || c.Name == name) && (path == null || c.Path == path) && !c.Expired)
            {
                c.Expired = true;
                n++;
            }
        }
        return n;
    }
}
