namespace ZawHTTP.Tests.Unit;

public class CurlTests
{
    static string? Header(HttpRequestModel m, string name) =>
        m.Headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))?.Value;

    [Fact]
    public void Imports_a_simple_get()
    {
        var m = Curl.Import("curl https://api.example.com/users?page=2");
        Assert.Equal("GET", m.Method);
        Assert.Equal("https://api.example.com/users?page=2", m.Url);
        Assert.Null(m.Body);
    }

    [Fact]
    public void Imports_bash_style_from_browser_devtools()
    {
        var m = Curl.Import("""
            curl 'https://api.example.com/items' \
              -H 'accept: application/json' \
              -H 'content-type: application/json' \
              -H $'x-note: it\'s é' \
              --data-raw '{"name":"Widget","price":9.5}' \
              --compressed
            """);
        Assert.Equal("POST", m.Method);
        Assert.Equal("https://api.example.com/items", m.Url);
        Assert.Equal("application/json", Header(m, "accept"));
        Assert.Equal("it's é", Header(m, "x-note"));
        Assert.Equal("raw", m.Body!.Mode);
        Assert.Equal("json", m.Body.RawType);
        Assert.Equal("""{"name":"Widget","price":9.5}""", m.Body.Raw);
    }

    [Fact]
    public void Imports_windows_cmd_style_from_browser_devtools()
    {
        var m = Curl.Import("curl ^\"https://api.example.com/items?q=a^&b=1^\" ^\n  -H ^\"accept: */*^\" ^\n  --data-raw ^\"^{^\\^\"id^\\^\":1^}^\"");
        Assert.Equal("POST", m.Method);
        Assert.Equal("https://api.example.com/items?q=a&b=1", m.Url);
        Assert.Equal("*/*", Header(m, "accept"));
        Assert.Equal("""{"id":1}""", m.Body!.Raw);
    }

    [Fact]
    public void Imports_options_methods_auth_and_bundled_flags()
    {
        var m = Curl.Import("curl -sSLk -XPATCH -u admin:s3:cret -A 'Agent/1' -e https://ref.example -b 'a=1; b=2' -m 2.5 \"http://localhost:8080/x\"");
        Assert.Equal("PATCH", m.Method);
        Assert.Equal("http://localhost:8080/x", m.Url);
        Assert.Equal("basic", m.Auth!.Type);
        Assert.Equal("admin", m.Auth.Username);
        Assert.Equal("s3:cret", m.Auth.Password);
        Assert.Equal("Agent/1", Header(m, "User-Agent"));
        Assert.Equal("https://ref.example", Header(m, "Referer"));
        Assert.Equal("a=1; b=2", Header(m, "Cookie"));
        Assert.False(m.Settings!.VerifySsl);
        Assert.Equal(2500, m.Settings.TimeoutMs);
    }

    [Fact]
    public void Form_data_becomes_a_urlencoded_body()
    {
        var m = Curl.Import("curl https://x.example/login -d 'user=jan%20k&pass=a%2Bb' --data-urlencode 'note=a b&c'");
        Assert.Equal("POST", m.Method);
        Assert.Equal("urlencoded", m.Body!.Mode);
        Assert.Equal(["user=jan k", "pass=a+b", "note=a b&c"], m.Body.Urlencoded!.Select(f => $"{f.Key}={f.Value}"));
    }

    [Fact]
    public void Multipart_fields_and_files()
    {
        var m = Curl.Import("curl -F 'title=Report' -F 'file=@/tmp/report.pdf;type=application/pdf' -H 'Content-Type: multipart/form-data; boundary=x' https://x.example/upload");
        Assert.Equal("POST", m.Method);
        Assert.Equal("formdata", m.Body!.Mode);
        Assert.Equal("text", m.Body.Form![0].Type);
        Assert.Equal("Report", m.Body.Form[0].Value);
        Assert.Equal("file", m.Body.Form[1].Type);
        Assert.Equal("/tmp/report.pdf", m.Body.Form[1].Value);
        Assert.Null(Header(m, "Content-Type")); // a copied boundary would not match the generated one
    }

    [Fact]
    public void Get_flag_moves_data_into_the_query_and_head_flag_sets_head()
    {
        var m = Curl.Import("curl -G https://x.example/search -d q=zaw --data-urlencode 'tag=a b'");
        Assert.Equal("GET", m.Method);
        Assert.Equal("https://x.example/search?q=zaw&tag=a%20b", m.Url);
        Assert.Null(m.Body);
        Assert.Equal("HEAD", Curl.Import("curl -I https://x.example").Method);
    }

    [Fact]
    public void Json_option_and_upload_file()
    {
        var m = Curl.Import("curl --json '{\"a\":1}' https://x.example");
        Assert.Equal("POST", m.Method);
        Assert.Equal("application/json", Header(m, "Content-Type"));
        Assert.Equal("json", m.Body!.RawType);
        var u = Curl.Import("curl -T ./data.bin https://x.example/put");
        Assert.Equal("PUT", u.Method);
        Assert.Equal("binary", u.Body!.Mode);
        Assert.Equal("./data.bin", u.Body.File);
    }

    [Fact]
    public void Rejects_text_that_is_not_curl()
    {
        Assert.Throws<ApiException>(() => Curl.Import("wget https://x.example"));
        Assert.Throws<ApiException>(() => Curl.Import("curl -H 'a: b'"));
    }

    [Fact]
    public void Export_produces_a_command_that_imports_back_to_the_same_request()
    {
        var m = new HttpRequestModel
        {
            Method = "PUT",
            Url = "https://{{host}}/items/7",
            Headers = [new KeyValue("X-Trace", "it's {{id}}"), new KeyValue("X-Off", "no", enabled: false)],
            Body = new RequestBody { Mode = "raw", RawType = "json", Raw = "{\n  \"name\": \"O'Brien\"\n}" },
            Auth = new RequestAuth { Type = "bearer", Token = "{{token}}" },
            Settings = new RequestSettings { VerifySsl = false },
        };
        var vars = new List<KeyValue> { new("host", "api.example"), new("id", "42"), new("token", "abc") };
        var text = Curl.Export(m, vars);
        Assert.Contains("--insecure", text);
        Assert.DoesNotContain("X-Off", text);

        var back = Curl.Import(text);
        Assert.Equal("PUT", back.Method);
        Assert.Equal("https://api.example/items/7", back.Url);
        Assert.Equal("it's 42", Header(back, "X-Trace"));
        Assert.Equal("Bearer abc", Header(back, "Authorization"));
        Assert.Equal("application/json", Header(back, "Content-Type"));
        Assert.Equal("{\n  \"name\": \"O'Brien\"\n}", back.Body!.Raw);
        Assert.False(back.Settings!.VerifySsl);
    }

    [Fact]
    public void Export_without_variables_keeps_placeholders()
    {
        var text = Curl.Export(new HttpRequestModel { Url = "{{base}}/ping" });
        Assert.Equal("curl \\\n  --url '{{base}}/ping' \\\n  --location", text);
    }
}
