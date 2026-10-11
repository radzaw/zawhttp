using System.Text.Json.Serialization;

namespace ZawHTTP;

/// <summary>A row of a key/value table: query parameter, header, form field or variable.</summary>
public sealed class KeyValue
{
    public string? Key { get; set; }
    public string? Value { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Form-data fields only: "text" or "file" (Value is then a local file path).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    public KeyValue() { }
    public KeyValue(string? key, string? value, bool enabled = true) { Key = key; Value = value; Enabled = enabled; }
}

public sealed class RequestBody
{
    /// <summary>none, raw, urlencoded, formdata or binary.</summary>
    public string Mode { get; set; } = "none";
    public string? Raw { get; set; }
    /// <summary>Raw mode: json, text, xml, html or javascript (picks the default Content-Type).</summary>
    public string RawType { get; set; } = "json";
    public List<KeyValue>? Urlencoded { get; set; }
    public List<KeyValue>? Form { get; set; }
    /// <summary>Binary mode: local file path.</summary>
    public string? File { get; set; }
}

public sealed class RequestAuth
{
    /// <summary>none, basic, bearer or apikey.</summary>
    public string Type { get; set; } = "none";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }
    public string? Key { get; set; }
    public string? Value { get; set; }
    /// <summary>API key: "header" or "query".</summary>
    public string In { get; set; } = "header";
}

public sealed class RequestSettings
{
    public int TimeoutMs { get; set; } = 30000;
    public bool FollowRedirects { get; set; } = true;
    public int MaxRedirects { get; set; } = 10;
    public bool VerifySsl { get; set; } = true;
}

/// <summary>A request as edited in a tab and stored in collections. Templates may contain {{variables}}.</summary>
public sealed class HttpRequestModel
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    /// <summary>The query-parameter table. The UI keeps it in sync with Url; the backend only sends Url.</summary>
    public List<KeyValue>? Params { get; set; }
    public List<KeyValue>? Headers { get; set; }
    public RequestBody? Body { get; set; }
    public RequestAuth? Auth { get; set; }
    public RequestSettings? Settings { get; set; }
}

/// <summary>A node of the collections tree: collection, folder or request.</summary>
public sealed class CollectionNode
{
    public string Id { get; set; } = NewId();
    /// <summary>collection, folder or request.</summary>
    public string Type { get; set; } = "request";
    public string Name { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CollectionNode>? Items { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HttpRequestModel? Request { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("n")[..16];
}

public sealed class EnvironmentModel
{
    public string Id { get; set; } = CollectionNode.NewId();
    public string Name { get; set; } = "";
    public List<KeyValue> Variables { get; set; } = [];
}

/// <summary>Send: the request template, the variables to resolve it with (later entries win) and an id to cancel it by.</summary>
public sealed record SendRequest(HttpRequestModel Request, List<KeyValue>? Variables = null, string? RunId = null, string? Name = null);
public sealed record CancelRequest(string RunId);
public sealed record CurlExportRequest(HttpRequestModel Request, List<KeyValue>? Variables = null);
public sealed record ImportRequest(string Text);

public sealed record HeaderLine(string Name, string Value);
public sealed record RedirectHop(int Status, string Method, string Url, string? Location);
public sealed record CookieInfo(string Name, string Value, string? Domain, string? Path, string? Expires, bool Secure, bool HttpOnly, string? SameSite);

/// <summary>What was actually sent (after variables, auth and defaults), for the timeline.</summary>
public sealed record SentRequest(string Method, string Url, List<HeaderLine> Headers, long BodySize, string? BodyPreview);

public sealed record HttpResult(
    string Id,
    int Status,
    string Reason,
    string HttpVersion,
    string Url,
    List<HeaderLine> Headers,
    List<CookieInfo> Cookies,
    string? ContentType,
    long Size,
    bool Truncated,
    bool IsText,
    string? Text,
    bool TextTruncated,
    double TtfbMs,
    double TotalMs,
    List<RedirectHop> Redirects,
    SentRequest Sent,
    List<string> Unresolved);

public sealed record HistoryEntry(string Id, long Time, string Method, string Url, int? Status, double? Ms, string? Error, string? Name, HttpRequestModel Request);
