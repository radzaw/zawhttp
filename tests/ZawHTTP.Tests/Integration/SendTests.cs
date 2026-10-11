using System.Net;
using System.Text.Json;
using ZawHTTP.Tests.Infrastructure;

namespace ZawHTTP.Tests.Integration;

/// <summary>Requests sent through the API to a local echo server.</summary>
public sealed class SendTests : IAsyncLifetime
{
    TestApp app = null!;
    EchoServer echo = null!;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        echo = await EchoServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await app.DisposeAsync();
        app.DeleteConfig();
        await echo.DisposeAsync();
    }

    Task<ApiResult> Send(HttpRequestModel r, params KeyValue[] vars) => app.PostAsync("/send", new { request = r, variables = vars });

    static JsonElement Echoed(JsonElement res) => JsonDocument.Parse(res.GetProperty("text").GetString()!).RootElement;

    [Fact]
    public async Task Get_with_variables_headers_and_defaults()
    {
        var res = (await Send(new HttpRequestModel
        {
            Url = "{{base}}/echo/users?id={{id}}",
            Headers = [new KeyValue("X-Trace", "{{id}}-t"), new KeyValue("X-Off", "1", enabled: false)],
        }, new KeyValue("base", echo.Url), new KeyValue("id", "42"))).Expect();

        Assert.Equal(200, res.GetProperty("status").GetInt32());
        Assert.Equal("OK", res.GetProperty("reason").GetString());
        Assert.Equal("HTTP/1.1", res.GetProperty("httpVersion").GetString());
        Assert.True(res.GetProperty("isText").GetBoolean());
        Assert.Contains("application/json", res.GetProperty("contentType").GetString());
        var e = Echoed(res);
        Assert.Equal("GET", e.GetProperty("method").GetString());
        Assert.Equal("/echo/users", e.GetProperty("path").GetString());
        Assert.Equal("?id=42", e.GetProperty("query").GetString());
        var h = e.GetProperty("headers");
        Assert.Equal("42-t", h.GetProperty("x-trace").GetString());
        Assert.False(h.TryGetProperty("x-off", out _));
        Assert.StartsWith("ZawHTTP/", h.GetProperty("user-agent").GetString());
        Assert.Equal("*/*", h.GetProperty("accept").GetString());
        Assert.True(res.GetProperty("totalMs").GetDouble() >= res.GetProperty("ttfbMs").GetDouble());
        Assert.Empty(res.GetProperty("unresolved").EnumerateArray());
    }

    [Fact]
    public async Task Unresolved_variables_are_reported()
    {
        var res = (await Send(new HttpRequestModel { Url = echo.Url + "/echo/x", Headers = [new KeyValue("X-A", "{{nope}}")] })).Expect();
        Assert.Equal(["nope"], res.GetProperty("unresolved").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("{{nope}}", Echoed(res).GetProperty("headers").GetProperty("x-a").GetString());
    }

    [Fact]
    public async Task Raw_json_body_gets_a_content_type_unless_one_is_set()
    {
        var res = (await Send(new HttpRequestModel
        {
            Method = "POST",
            Url = echo.Url + "/echo/items",
            Body = new RequestBody { Mode = "raw", RawType = "json", Raw = "{\"n\":\"{{name}}\"}" },
        }, new KeyValue("name", "Zaż"))).Expect();
        var e = Echoed(res);
        Assert.Equal("{\"n\":\"Zaż\"}", e.GetProperty("body").GetString());
        Assert.Equal("application/json", e.GetProperty("headers").GetProperty("content-type").GetString());

        var own = Echoed((await Send(new HttpRequestModel
        {
            Method = "POST",
            Url = echo.Url + "/echo/items",
            Headers = [new KeyValue("Content-Type", "application/vnd.api+json")],
            Body = new RequestBody { Mode = "raw", RawType = "json", Raw = "{}" },
        })).Expect());
        Assert.Equal("application/vnd.api+json", own.GetProperty("headers").GetProperty("content-type").GetString());
    }

    [Fact]
    public async Task Urlencoded_and_multipart_bodies()
    {
        var form = Echoed((await Send(new HttpRequestModel
        {
            Method = "POST",
            Url = echo.Url + "/echo/form",
            Body = new RequestBody { Mode = "urlencoded", Urlencoded = [new("a", "1 2"), new("b", "x&y"), new("off", "1", enabled: false)] },
        })).Expect());
        Assert.Equal("1 2", form.GetProperty("form").GetProperty("a").GetString());
        Assert.Equal("x&y", form.GetProperty("form").GetProperty("b").GetString());
        Assert.False(form.GetProperty("form").TryGetProperty("off", out _));

        var file = Path.Combine(app.ConfigDir, "upload.txt");
        await File.WriteAllTextAsync(file, "file content");
        var mp = Echoed((await Send(new HttpRequestModel
        {
            Method = "POST",
            Url = echo.Url + "/echo/upload",
            Body = new RequestBody { Mode = "formdata", Form = [new("title", "Report") { Type = "text" }, new("doc", file) { Type = "file" }] },
        })).Expect());
        Assert.Equal("Report", mp.GetProperty("form").GetProperty("title").GetString());
        Assert.Equal("upload.txt|text/plain|file content", mp.GetProperty("files").GetProperty("doc").GetString());
        Assert.StartsWith("multipart/form-data; boundary=----ZawHTTPBoundary", mp.GetProperty("headers").GetProperty("content-type").GetString());
    }

    [Fact]
    public async Task Missing_upload_file_is_a_clear_error()
    {
        var r = await Send(new HttpRequestModel { Method = "POST", Url = echo.Url + "/echo/x", Body = new RequestBody { Mode = "binary", File = @"C:\nope\missing.bin" } });
        Assert.False(r.Ok);
        Assert.Contains("File not found", r.Error);
    }

    [Fact]
    public async Task Auth_types()
    {
        var basic = Echoed((await Send(new HttpRequestModel { Url = echo.Url + "/echo/a", Auth = new RequestAuth { Type = "basic", Username = "jan", Password = "{{pw}}" } }, new KeyValue("pw", "sekret"))).Expect());
        Assert.Equal("Basic " + Convert.ToBase64String("jan:sekret"u8.ToArray()), basic.GetProperty("headers").GetProperty("authorization").GetString());

        var bearer = Echoed((await Send(new HttpRequestModel { Url = echo.Url + "/echo/a", Auth = new RequestAuth { Type = "bearer", Token = "abc" } })).Expect());
        Assert.Equal("Bearer abc", bearer.GetProperty("headers").GetProperty("authorization").GetString());

        var key = Echoed((await Send(new HttpRequestModel { Url = echo.Url + "/echo/a?x=1", Auth = new RequestAuth { Type = "apikey", Key = "api_key", Value = "k 1", In = "query" } })).Expect());
        Assert.Equal("?x=1&api_key=k%201", key.GetProperty("query").GetString());

        // A header set by hand wins over the auth tab.
        var own = Echoed((await Send(new HttpRequestModel { Url = echo.Url + "/echo/a", Headers = [new("Authorization", "Custom 1")], Auth = new RequestAuth { Type = "bearer", Token = "abc" } })).Expect());
        Assert.Equal("Custom 1", own.GetProperty("headers").GetProperty("authorization").GetString());
    }

    [Fact]
    public async Task Redirects_are_followed_hop_by_hop()
    {
        // 302 after POST continues as GET without the body; 307 keeps the method and body.
        var r302 = (await Send(new HttpRequestModel
        {
            Method = "POST",
            Url = $"{echo.Url}/redirect/302?to=/redirect/307%3Fto%3D/echo/final",
            Body = new RequestBody { Mode = "raw", RawType = "text", Raw = "payload" },
        })).Expect();
        var hops = r302.GetProperty("redirects").EnumerateArray().ToList();
        Assert.Equal(2, hops.Count);
        Assert.Equal(302, hops[0].GetProperty("status").GetInt32());
        Assert.Equal("POST", hops[0].GetProperty("method").GetString());
        Assert.Equal("GET", hops[1].GetProperty("method").GetString());
        var e = Echoed(r302);
        Assert.Equal("GET", e.GetProperty("method").GetString());
        Assert.Equal("", e.GetProperty("body").GetString());
        Assert.EndsWith("/echo/final", r302.GetProperty("url").GetString());

        var r307 = Echoed((await Send(new HttpRequestModel
        {
            Method = "POST",
            Url = $"{echo.Url}/redirect/307?to=/echo/kept",
            Body = new RequestBody { Mode = "raw", RawType = "text", Raw = "payload" },
        })).Expect());
        Assert.Equal("POST", r307.GetProperty("method").GetString());
        Assert.Equal("payload", r307.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Redirects_are_not_followed_when_switched_off()
    {
        var res = (await Send(new HttpRequestModel { Url = echo.Url + "/redirect/301", Settings = new RequestSettings { FollowRedirects = false } })).Expect();
        Assert.Equal(301, res.GetProperty("status").GetInt32());
        Assert.Empty(res.GetProperty("redirects").EnumerateArray());
        Assert.Contains(res.GetProperty("headers").EnumerateArray(), h => h.GetProperty("name").GetString() == "Location");
    }

    [Fact]
    public async Task Cookies_are_kept_in_the_jar_and_sent_again()
    {
        var set = (await Send(new HttpRequestModel { Url = echo.Url + "/cookie/set?name=sid&value=abc123" })).Expect();
        var c = Assert.Single(set.GetProperty("cookies").EnumerateArray());
        Assert.Equal("sid", c.GetProperty("name").GetString());
        Assert.True(c.GetProperty("httpOnly").GetBoolean());

        var next = (await Send(new HttpRequestModel { Url = echo.Url + "/echo/x" })).Expect();
        Assert.Equal("sid=abc123", Echoed(next).GetProperty("headers").GetProperty("cookie").GetString());
        Assert.Contains(next.GetProperty("sent").GetProperty("headers").EnumerateArray(), h => h.GetProperty("name").GetString() == "Cookie");

        var jar = (await app.GetAsync("/cookies")).Expect();
        Assert.Equal("sid", Assert.Single(jar.EnumerateArray()).GetProperty("name").GetString());
        (await app.CallAsync(HttpMethod.Delete, "/cookies")).Expect();
        Assert.Empty((await app.GetAsync("/cookies")).Expect().EnumerateArray());
        var after = Echoed((await Send(new HttpRequestModel { Url = echo.Url + "/echo/x" })).Expect());
        Assert.False(after.GetProperty("headers").TryGetProperty("cookie", out _));
    }

    [Fact]
    public async Task Timeout_and_cancel()
    {
        var t = await Send(new HttpRequestModel { Url = echo.Url + "/slow?ms=5000", Settings = new RequestSettings { TimeoutMs = 200 } });
        Assert.False(t.Ok);
        Assert.Equal("The request timed out after 200 ms.", t.Error);

        var running = app.PostAsync("/send", new { request = new HttpRequestModel { Url = echo.Url + "/slow?ms=10000" }, runId = "run-1" });
        var cancelled = false;
        for (var i = 0; i < 50 && !cancelled; i++)
        {
            await Task.Delay(50);
            cancelled = (await app.PostAsync("/cancel", new { runId = "run-1" })).Expect().GetProperty("cancelled").GetBoolean();
        }
        Assert.True(cancelled);
        var r = await running;
        Assert.False(r.Ok);
        Assert.Equal("The request was cancelled.", r.Error);
    }

    [Fact]
    public async Task Charsets_binary_bodies_and_downloads()
    {
        var latin = (await Send(new HttpRequestModel { Url = echo.Url + "/latin2" })).Expect();
        Assert.Equal("Zażółć gęślą jaźń", latin.GetProperty("text").GetString());

        var bin = (await Send(new HttpRequestModel { Url = echo.Url + "/binary" })).Expect();
        Assert.False(bin.GetProperty("isText").GetBoolean());
        Assert.Equal(JsonValueKind.Null, bin.GetProperty("text").ValueKind);
        Assert.Equal(8, bin.GetProperty("size").GetInt64());
        var bytes = await app.Http.GetByteArrayAsync($"/api/response/{bin.GetProperty("id").GetString()}");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3 }, bytes);

        var csv = (await Send(new HttpRequestModel { Url = echo.Url + "/download" })).Expect();
        using var dl = await app.Http.GetAsync($"/api/response/{csv.GetProperty("id").GetString()}?download=true");
        Assert.Equal("report.csv", dl.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("a,b\n1,2", await dl.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Error_statuses_are_responses_not_failures()
    {
        var res = (await Send(new HttpRequestModel { Url = echo.Url + "/status/503" })).Expect();
        Assert.Equal(503, res.GetProperty("status").GetInt32());
        Assert.Equal("status 503", res.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Invalid_input_and_unreachable_hosts_give_readable_errors()
    {
        var empty = await Send(new HttpRequestModel { Url = "  " });
        Assert.Equal("Enter a URL to send the request to.", empty.Error);
        var ftp = await Send(new HttpRequestModel { Url = "ftp://x.example/file" });
        Assert.Contains("not a valid http:// or https:// URL", ftp.Error);
        var method = await Send(new HttpRequestModel { Method = "GE T", Url = echo.Url });
        Assert.Contains("not a valid HTTP method", method.Error);

        // Nothing listens on port 1.
        var down = await Send(new HttpRequestModel { Url = "http://127.0.0.1:1/", Settings = new RequestSettings { TimeoutMs = 5000 } });
        Assert.False(down.Ok);
        Assert.False(string.IsNullOrWhiteSpace(down.Error));
    }

    [Fact]
    public async Task A_url_without_scheme_gets_http()
    {
        var host = new Uri(echo.Url);
        var res = (await Send(new HttpRequestModel { Url = $"127.0.0.1:{host.Port}/echo/noscheme" })).Expect();
        Assert.Equal($"http://127.0.0.1:{host.Port}/echo/noscheme", res.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Sent_requests_are_recorded_in_history()
    {
        await Send(new HttpRequestModel { Url = echo.Url + "/echo/h1" });
        await Send(new HttpRequestModel { Url = "http://127.0.0.1:1/", Settings = new RequestSettings { TimeoutMs = 3000 } });
        var hist = (await app.GetAsync("/history")).Expect().EnumerateArray().ToList();
        Assert.Equal(2, hist.Count);
        Assert.Equal("http://127.0.0.1:1/", hist[0].GetProperty("url").GetString()); // newest first
        Assert.Equal(JsonValueKind.Null, hist[0].GetProperty("status").ValueKind);
        Assert.False(string.IsNullOrEmpty(hist[0].GetProperty("error").GetString()));
        Assert.Equal(200, hist[1].GetProperty("status").GetInt32());
        Assert.Equal(echo.Url + "/echo/h1", hist[1].GetProperty("request").GetProperty("url").GetString());

        (await app.CallAsync(HttpMethod.Delete, "/history/" + hist[0].GetProperty("id").GetString())).Expect();
        Assert.Single((await app.GetAsync("/history")).Expect().EnumerateArray());
        (await app.CallAsync(HttpMethod.Delete, "/history")).Expect();
        Assert.Empty((await app.GetAsync("/history")).Expect().EnumerateArray());
    }

    [Fact]
    public async Task Export_curl_through_the_api()
    {
        var r = (await app.PostAsync("/export/curl", new { request = new HttpRequestModel { Method = "DELETE", Url = "{{u}}/x" }, variables = new[] { new KeyValue("u", "http://h") } })).Expect();
        Assert.Equal("curl \\\n  --request DELETE \\\n  --url 'http://h/x' \\\n  --location", r.GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.OK, (await app.Http.GetAsync("/")).StatusCode);
    }
}
