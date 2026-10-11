using System.Globalization;
using System.Text;

namespace ZawHTTP;

/// <summary>Converts between requests and cURL command lines (bash syntax, plus Windows cmd from "Copy as cURL (cmd)").</summary>
public static class Curl
{
    // ------------------------------------------------------------------ export

    /// <summary>A bash cURL command for the request. With variables, {{placeholders}} are resolved first.</summary>
    public static string Export(HttpRequestModel m, List<KeyValue>? variables = null)
    {
        var vars = new Variables(variables);
        string V(string? s) => variables == null ? s ?? "" : vars.Apply(s);

        var url = V(m.Url).Trim();
        var auth = m.Auth ?? new RequestAuth();
        if (auth.Type == "apikey" && auth.In == "query" && !string.IsNullOrWhiteSpace(auth.Key))
            url += (url.Contains('?') ? "&" : "?") + Uri.EscapeDataString(V(auth.Key)) + "=" + Uri.EscapeDataString(V(auth.Value));

        var method = V(m.Method).Trim().ToUpperInvariant();
        var body = m.Body ?? new RequestBody();
        var parts = new List<string> { "curl" };
        var hasBody = body.Mode is "raw" or "urlencoded" or "formdata" or "binary";
        if (method == "HEAD") parts.Add("--head");
        else if (method != "GET" || hasBody) parts.Add("--request " + method);
        parts.Add("--url " + Quote(url));
        if (m.Settings?.FollowRedirects != false) parts.Add("--location");
        if (m.Settings?.VerifySsl == false) parts.Add("--insecure");

        var headers = (m.Headers ?? []).Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Key)).ToList();
        foreach (var h in headers) parts.Add("--header " + Quote($"{V(h.Key)}: {V(h.Value)}"));
        bool Has(string n) => headers.Any(h => V(h.Key).Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
        switch (auth.Type)
        {
            case "basic": parts.Add("--user " + Quote($"{V(auth.Username)}:{V(auth.Password)}")); break;
            case "bearer" when !Has("Authorization"): parts.Add("--header " + Quote("Authorization: Bearer " + V(auth.Token))); break;
            case "apikey" when auth.In != "query" && !string.IsNullOrWhiteSpace(auth.Key): parts.Add("--header " + Quote($"{V(auth.Key)}: {V(auth.Value)}")); break;
        }

        switch (body.Mode)
        {
            case "raw":
                if (!Has("Content-Type"))
                {
                    var ct = body.RawType switch { "json" => "application/json", "xml" => "application/xml", "html" => "text/html", "javascript" => "application/javascript", _ => "text/plain" };
                    parts.Add("--header " + Quote("Content-Type: " + ct));
                }
                parts.Add("--data-raw " + Quote(V(body.Raw)));
                break;
            case "urlencoded":
                foreach (var f in (body.Urlencoded ?? []).Where(f => f.Enabled && !string.IsNullOrEmpty(f.Key)))
                    parts.Add("--data-urlencode " + Quote($"{V(f.Key)}={V(f.Value)}"));
                break;
            case "formdata":
                foreach (var f in (body.Form ?? []).Where(f => f.Enabled && !string.IsNullOrEmpty(f.Key)))
                    parts.Add(f.Type == "file" ? "--form " + Quote($"{V(f.Key)}=@{V(f.Value)}") : "--form-string " + Quote($"{V(f.Key)}={V(f.Value)}"));
                break;
            case "binary":
                parts.Add("--data-binary " + Quote("@" + V(body.File)));
                break;
        }
        return string.Join(" \\\n  ", parts);
    }

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ------------------------------------------------------------------ import

    static readonly HashSet<string> IgnoredWithValue =
    [
        "-o", "--output", "-w", "--write-out", "--connect-timeout", "--retry", "--retry-delay", "--retry-max-time", "-x", "--proxy",
        "--resolve", "--cacert", "--capath", "--cert", "-E", "--key", "-c", "--cookie-jar", "-D", "--dump-header", "-r", "--range",
        "--limit-rate", "-y", "--speed-time", "-Y", "--speed-limit", "--max-redirs", "-U", "--proxy-user", "--interface", "--trace",
        "--trace-ascii", "--stderr", "-K", "--config", "--unix-socket", "--cert-type", "--key-type", "--pass", "--tls-max", "--ciphers",
    ];

    public static bool LooksLikeCurl(string text) => text.TrimStart().StartsWith("curl", StringComparison.OrdinalIgnoreCase)
        && (text.TrimStart().Length == 4 || char.IsWhiteSpace(text.TrimStart()[4]) || text.TrimStart()[4] == '.');

    /// <summary>Parses a cURL command into a request.</summary>
    public static HttpRequestModel Import(string command)
    {
        var args = Tokenize(command);
        if (args.Count == 0 || !args[0].StartsWith("curl", StringComparison.OrdinalIgnoreCase))
            throw new ApiException("This is not a cURL command (it must start with \"curl\").");

        string? method = null, url = null, file = null;
        bool head = false, get = false, insecure = false, json = false;
        int? timeout = null;
        var headers = new List<KeyValue>();
        var data = new List<(string Text, bool Encode)>();
        var form = new List<KeyValue>();
        RequestAuth? auth = null;

        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            string Value(string? attached) => attached ?? (i + 1 < args.Count ? args[++i] : throw new ApiException($"cURL option {a} needs a value."));

            // Long options: "--name value" or "--name=value".
            var opt = a;
            string? attached = null;
            if (a.StartsWith("--"))
            {
                var eq = a.IndexOf('=');
                if (eq > 0) { opt = a[..eq]; attached = a[(eq + 1)..]; }
            }
            else if (a.Length > 2 && a[0] == '-' && a[1] != '-')
            {
                // Short options can be bundled (-sSL) or carry their value (-XPOST).
                var takesValue = "XHdFuAebmoTwxrEDcKyY";
                var j = 1;
                for (; j < a.Length && !takesValue.Contains(a[j]); j++) Flag("-" + a[j]);
                if (j >= a.Length) continue;
                opt = "-" + a[j];
                attached = j + 1 < a.Length ? a[(j + 1)..] : null;
            }

            switch (opt)
            {
                case "-X" or "--request": method = Value(attached); break;
                case "-H" or "--header":
                    var hv = Value(attached);
                    var colon = hv.IndexOf(':');
                    if (colon > 0) headers.Add(new KeyValue(hv[..colon].Trim(), hv[(colon + 1)..].Trim()));
                    else if (hv.EndsWith(';')) headers.Add(new KeyValue(hv.TrimEnd(';').Trim(), ""));
                    break;
                case "-d" or "--data" or "--data-ascii" or "--data-binary" or "--data-raw":
                    var dv = Value(attached);
                    if (opt != "--data-raw" && dv.StartsWith('@')) file = dv[1..];
                    else data.Add((dv, false));
                    break;
                case "--data-urlencode": data.Add((Value(attached), true)); break;
                case "--json":
                    data.Add((Value(attached), false));
                    json = true;
                    break;
                case "-F" or "--form" or "--form-string":
                    var fv = Value(attached);
                    var feq = fv.IndexOf('=');
                    if (feq <= 0) break;
                    var fval = fv[(feq + 1)..];
                    if (opt != "--form-string" && (fval.StartsWith('@') || fval.StartsWith('<')))
                    {
                        var semi = fval.IndexOf(";type=", StringComparison.Ordinal);
                        form.Add(new KeyValue(fv[..feq], (semi > 0 ? fval[1..semi] : fval[1..]).Trim('"')) { Type = "file" });
                    }
                    else form.Add(new KeyValue(fv[..feq], opt == "--form-string" ? fval : fval.Trim('"')) { Type = "text" });
                    break;
                case "-u" or "--user":
                    var uv = Value(attached);
                    var uc = uv.IndexOf(':');
                    auth = new RequestAuth { Type = "basic", Username = uc < 0 ? uv : uv[..uc], Password = uc < 0 ? "" : uv[(uc + 1)..] };
                    break;
                case "-A" or "--user-agent": headers.Add(new KeyValue("User-Agent", Value(attached))); break;
                case "-e" or "--referer": headers.Add(new KeyValue("Referer", Value(attached))); break;
                case "-b" or "--cookie":
                    var cv = Value(attached);
                    if (cv.Contains('=')) headers.Add(new KeyValue("Cookie", cv));
                    break;
                case "-T" or "--upload-file":
                    file = Value(attached);
                    method ??= "PUT";
                    break;
                case "-m" or "--max-time":
                    if (double.TryParse(Value(attached), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)) timeout = (int)(secs * 1000);
                    break;
                case "--url": url = Value(attached); break;
                default:
                    if (IgnoredWithValue.Contains(opt)) { Value(attached); break; }
                    if (a.StartsWith('-') && a.Length > 1) { Flag(opt); break; }
                    url ??= a;
                    break;
            }
        }

        void Flag(string f)
        {
            switch (f)
            {
                case "-I" or "--head": head = true; break;
                case "-G" or "--get": get = true; break;
                case "-k" or "--insecure": insecure = true; break;
            }
        }

        if (string.IsNullOrWhiteSpace(url)) throw new ApiException("The cURL command has no URL.");
        var m = new HttpRequestModel { Url = url, Headers = headers, Auth = auth };
        if (insecure || timeout != null) m.Settings = new RequestSettings { VerifySsl = !insecure, TimeoutMs = timeout ?? 30000 };
        if (json)
        {
            if (!headers.Any(h => h.Key!.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))) headers.Add(new KeyValue("Content-Type", "application/json"));
            if (!headers.Any(h => h.Key!.Equals("Accept", StringComparison.OrdinalIgnoreCase))) headers.Add(new KeyValue("Accept", "application/json"));
        }

        if (get && data.Count > 0)
        {
            var q = string.Join("&", data.Select(d => d.Encode ? EncodePair(d.Text) : d.Text));
            m.Url += (m.Url.Contains('?') ? "&" : "?") + q;
            data.Clear();
        }

        var contentType = headers.FirstOrDefault(h => h.Key!.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value?.ToLowerInvariant() ?? "";
        if (form.Count > 0)
        {
            m.Body = new RequestBody { Mode = "formdata", Form = form };
            // The boundary is generated on send; a copied Content-Type would carry a stale one.
            headers.RemoveAll(h => h.Key!.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        }
        else if (file != null)
        {
            m.Body = new RequestBody { Mode = "binary", File = file };
        }
        else if (data.Count > 0)
        {
            var joined = string.Join("&", data.Select(d => d.Encode ? EncodePair(d.Text) : d.Text));
            var trimmed = joined.TrimStart();
            if (contentType.Contains("json") || (contentType.Length == 0 && (trimmed.StartsWith('{') || trimmed.StartsWith('['))))
                m.Body = new RequestBody { Mode = "raw", RawType = "json", Raw = joined };
            else if ((contentType.Length == 0 || contentType.Contains("x-www-form-urlencoded")) && TryParseForm(joined, out var pairs))
                m.Body = new RequestBody { Mode = "urlencoded", Urlencoded = pairs };
            else
                m.Body = new RequestBody { Mode = "raw", RawType = contentType.Contains("xml") ? "xml" : contentType.Contains("html") ? "html" : "text", Raw = joined };
        }

        m.Method = (method ?? (head ? "HEAD" : m.Body != null ? "POST" : "GET")).ToUpperInvariant();
        return m;
    }

    static string EncodePair(string s)
    {
        // curl --data-urlencode: "name=content" encodes the content; "content" alone is encoded whole.
        var eq = s.IndexOf('=');
        return eq < 0 ? Uri.EscapeDataString(s) : s[..eq] + "=" + Uri.EscapeDataString(s[(eq + 1)..]);
    }

    static bool TryParseForm(string s, out List<KeyValue> pairs)
    {
        pairs = [];
        foreach (var part in s.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            if (eq <= 0) return false;
            try
            {
                pairs.Add(new KeyValue(Uri.UnescapeDataString(part[..eq].Replace('+', ' ')), Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '))));
            }
            catch (UriFormatException)
            {
                return false;
            }
        }
        return pairs.Count > 0;
    }

    /// <summary>Splits a command line into words, honoring bash quoting or, for "Copy as cURL (cmd)", cmd's ^ escapes.</summary>
    internal static List<string> Tokenize(string text)
    {
        var cmd = text.Contains("^\"") || text.Contains(" ^\n") || text.Contains(" ^\r\n");
        if (cmd)
        {
            text = text.Replace("^\r\n", " ").Replace("^\n", " ");
            var sb0 = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '^' && i + 1 < text.Length) sb0.Append(text[++i]);
                else sb0.Append(text[i]);
            }
            text = sb0.ToString();
        }

        var words = new List<string>();
        var sb = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && !cmd && i + 1 < text.Length && (text[i + 1] == '\n' || (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')))
            {
                i += text[i + 1] == '\r' ? 2 : 1; // line continuation
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inWord) { words.Add(sb.ToString()); sb.Clear(); inWord = false; }
                continue;
            }
            inWord = true;
            if (c == '\'' && !cmd)
            {
                var end = text.IndexOf('\'', i + 1);
                if (end < 0) end = text.Length;
                sb.Append(text, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '$' && !cmd && i + 1 < text.Length && text[i + 1] == '\'')
            {
                i = AnsiC(text, i + 2, sb);
            }
            else if (c == '"')
            {
                // bash: \" \\ \$ \` are escapes; curl.exe from cmd: only \" is.
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || (!cmd && text[i + 1] is '\\' or '$' or '`'))) i++;
                    else if (text[i] == '\\' && !cmd && i + 1 < text.Length && text[i + 1] == '\n') { i++; continue; }
                    sb.Append(text[i]);
                }
            }
            else if (c == '\\' && !cmd && i + 1 < text.Length)
            {
                sb.Append(text[++i]);
            }
            else
            {
                sb.Append(c);
            }
        }
        if (inWord) words.Add(sb.ToString());
        return words;
    }

    /// <summary>Reads a bash $'...' string starting after the opening quote; returns the index of the closing quote.</summary>
    static int AnsiC(string text, int i, StringBuilder sb)
    {
        for (; i < text.Length && text[i] != '\''; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length) { sb.Append(text[i]); continue; }
            var e = text[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'x' when i + 2 < text.Length && int.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var x):
                    sb.Append((char)x);
                    i += 2;
                    break;
                case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, null, out var u):
                    sb.Append((char)u);
                    i += 4;
                    break;
                default: sb.Append(e); break;
            }
        }
        return i;
    }
}
