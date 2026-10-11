using System.Text.Json;

namespace ZawHTTP;

/// <summary>What an import produced: a single request (cURL) or collections and environments.</summary>
public sealed record ImportResult(string Kind, HttpRequestModel? Request = null, List<CollectionNode>? Collections = null, List<EnvironmentModel>? Environments = null);

/// <summary>
/// Imports pasted or opened text: a cURL command, a Postman collection (v2.0 / v2.1), a Postman environment
/// or a ZawHTTP export. Postman's inherited auth is applied to each request, collection variables become an environment.
/// </summary>
public static class Importer
{
    public static ImportResult Import(string text)
    {
        text = text.Trim().TrimStart('﻿');
        if (text.Length == 0) throw new ApiException("Nothing to import.");
        if (Curl.LooksLikeCurl(text)) return new ImportResult("request", Request: Curl.Import(text));

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ApiException($"This is neither a cURL command nor valid JSON: {ex.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object) throw new ApiException("Unrecognized file: expected a JSON object.");

        if (root.TryGetProperty("zawhttp", out _))
        {
            var native = root.Deserialize<NativeExport>(Workspace.Json) ?? new NativeExport();
            foreach (var c in native.Collections ?? []) Renew(c);
            foreach (var e in native.Environments ?? []) e.Id = CollectionNode.NewId();
            return new ImportResult("workspace", Collections: native.Collections ?? [], Environments: native.Environments ?? []);
        }
        if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Array)
            return PostmanCollection(root);
        if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
            return new ImportResult("environment", Environments: [new EnvironmentModel { Name = Str(root, "name") ?? "Imported environment", Variables = Vars(values) }]);
        throw new ApiException("Unrecognized file. ZawHTTP imports cURL commands, Postman collections and environments, and its own exports.");
    }

    public sealed class NativeExport
    {
        public int Zawhttp { get; set; } = 1;
        public List<CollectionNode>? Collections { get; set; }
        public List<EnvironmentModel>? Environments { get; set; }
    }

    /// <summary>Imported nodes get fresh ids, so importing the same file twice doesn't create clashes.</summary>
    static void Renew(CollectionNode n)
    {
        n.Id = CollectionNode.NewId();
        foreach (var c in n.Items ?? []) Renew(c);
    }

    // ------------------------------------------------------------------ Postman

    static ImportResult PostmanCollection(JsonElement root)
    {
        var info = root.TryGetProperty("info", out var i) ? i : default;
        var name = (info.ValueKind == JsonValueKind.Object ? Str(info, "name") : null) ?? Str(root, "name") ?? "Imported collection";
        var auth = root.TryGetProperty("auth", out var a) ? Auth(a) : null;
        var col = new CollectionNode { Type = "collection", Name = name, Items = Items(root.GetProperty("item"), auth) };
        var envs = new List<EnvironmentModel>();
        if (root.TryGetProperty("variable", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
            envs.Add(new EnvironmentModel { Name = name + " variables", Variables = Vars(v) });
        return new ImportResult("collection", Collections: [col], Environments: envs);
    }

    static List<CollectionNode> Items(JsonElement items, RequestAuth? inherited)
    {
        var list = new List<CollectionNode>();
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            var name = Str(it, "name") ?? "Untitled";
            if (it.TryGetProperty("item", out var sub) && sub.ValueKind == JsonValueKind.Array)
            {
                var auth = it.TryGetProperty("auth", out var fa) ? Auth(fa) : inherited;
                list.Add(new CollectionNode { Type = "folder", Name = name, Items = Items(sub, auth) });
            }
            else if (it.TryGetProperty("request", out var req))
            {
                list.Add(new CollectionNode { Type = "request", Name = name, Request = Request(req, inherited) });
            }
        }
        return list;
    }

    static HttpRequestModel Request(JsonElement r, RequestAuth? inherited)
    {
        if (r.ValueKind == JsonValueKind.String) return new HttpRequestModel { Url = r.GetString() ?? "" };
        var m = new HttpRequestModel
        {
            Method = (Str(r, "method") ?? "GET").ToUpperInvariant(),
            Url = r.TryGetProperty("url", out var u) ? Url(u) : "",
            Headers = r.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.Array ? Vars(h) : [],
            Auth = r.TryGetProperty("auth", out var a) ? Auth(a) : inherited,
        };
        if (r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object) m.Body = Body(b);
        return m;
    }

    static string Url(JsonElement u)
    {
        if (u.ValueKind == JsonValueKind.String) return u.GetString() ?? "";
        if (u.ValueKind != JsonValueKind.Object) return "";
        if (Str(u, "raw") is { } raw) return raw;
        var url = "";
        if (Str(u, "protocol") is { } proto) url = proto + "://";
        url += Join(u, "host", ".");
        if (Str(u, "port") is { } port) url += ":" + port;
        var path = Join(u, "path", "/");
        if (path.Length > 0) url += "/" + path;
        if (u.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Array)
        {
            var qs = Vars(q).Where(x => x.Enabled).Select(x => x.Key + (x.Value == null ? "" : "=" + x.Value));
            var s = string.Join("&", qs);
            if (s.Length > 0) url += "?" + s;
        }
        return url;
    }

    static string Join(JsonElement o, string prop, string sep) =>
        !o.TryGetProperty(prop, out var p) ? ""
        : p.ValueKind == JsonValueKind.String ? p.GetString() ?? ""
        : p.ValueKind == JsonValueKind.Array ? string.Join(sep, p.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString()))
        : "";

    static RequestBody? Body(JsonElement b)
    {
        switch (Str(b, "mode"))
        {
            case "raw":
                var lang = b.TryGetProperty("options", out var o) && o.TryGetProperty("raw", out var ro) ? Str(ro, "language") : null;
                var raw = Str(b, "raw") ?? "";
                lang ??= raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[') ? "json" : "text";
                return new RequestBody { Mode = "raw", Raw = raw, RawType = lang is "json" or "xml" or "html" or "javascript" ? lang : "text" };
            case "urlencoded":
                return new RequestBody { Mode = "urlencoded", Urlencoded = b.TryGetProperty("urlencoded", out var ue) ? Vars(ue) : [] };
            case "formdata":
                var form = new List<KeyValue>();
                if (b.TryGetProperty("formdata", out var fd) && fd.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fd.EnumerateArray())
                    {
                        var file = Str(f, "type") == "file";
                        var value = file
                            ? f.TryGetProperty("src", out var src) ? src.ValueKind == JsonValueKind.Array ? src.EnumerateArray().FirstOrDefault().GetString() : Str(f, "src") : ""
                            : Str(f, "value");
                        form.Add(new KeyValue(Str(f, "key"), value ?? "", !Bool(f, "disabled")) { Type = file ? "file" : "text" });
                    }
                }
                return new RequestBody { Mode = "formdata", Form = form };
            case "file":
                return new RequestBody { Mode = "binary", File = b.TryGetProperty("file", out var fl) && fl.ValueKind == JsonValueKind.Object ? Str(fl, "src") : null };
            case "graphql":
                var gql = b.TryGetProperty("graphql", out var g) ? g : default;
                var query = gql.ValueKind == JsonValueKind.Object ? Str(gql, "query") ?? "" : "";
                var gv = gql.ValueKind == JsonValueKind.Object ? Str(gql, "variables") : null;
                object? variables = null;
                if (!string.IsNullOrWhiteSpace(gv))
                {
                    try { variables = JsonSerializer.Deserialize<JsonElement>(gv); } catch (JsonException) { variables = gv; }
                }
                var json = JsonSerializer.Serialize(new { query, variables }, new JsonSerializerOptions { WriteIndented = true });
                return new RequestBody { Mode = "raw", RawType = "json", Raw = json };
            default:
                return null;
        }
    }

    static RequestAuth? Auth(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object) return null;
        var type = Str(a, "type");
        // v2.1 stores the settings as [{key, value}], v2.0 as an object.
        string? Get(string key)
        {
            if (type == null || !a.TryGetProperty(type, out var s)) return null;
            if (s.ValueKind == JsonValueKind.Object) return Str(s, key);
            if (s.ValueKind == JsonValueKind.Array)
                foreach (var e in s.EnumerateArray())
                    if (Str(e, "key") == key) return Str(e, "value");
            return null;
        }
        return type switch
        {
            "basic" => new RequestAuth { Type = "basic", Username = Get("username"), Password = Get("password") },
            "bearer" => new RequestAuth { Type = "bearer", Token = Get("token") },
            "apikey" => new RequestAuth { Type = "apikey", Key = Get("key"), Value = Get("value"), In = Get("in") == "query" ? "query" : "header" },
            _ => new RequestAuth { Type = "none" },
        };
    }

    static List<KeyValue> Vars(JsonElement arr)
    {
        var list = new List<KeyValue>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var enabled = e.TryGetProperty("enabled", out var en) ? en.ValueKind != JsonValueKind.False : !Bool(e, "disabled");
            list.Add(new KeyValue(Str(e, "key"), Str(e, "value") ?? "", enabled));
        }
        return list;
    }

    static string? Str(JsonElement o, string prop) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.ToString(),
                _ => null,
            }
            : null;

    static bool Bool(JsonElement o, string prop) => o.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.True;
}
