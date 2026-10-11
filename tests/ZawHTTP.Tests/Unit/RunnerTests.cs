using System.Text;

namespace ZawHTTP.Tests.Unit;

public class RunnerTests
{
    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("text/html", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("application/xml", true)]
    [InlineData("image/png", false)]
    [InlineData("application/pdf", false)]
    [InlineData("application/octet-stream", false)]
    public void Content_types_decide_whether_a_body_is_text(string type, bool text) =>
        Assert.Equal(text, HttpRunner.LooksTextual(type, "abc"u8));

    [Fact]
    public void Without_a_content_type_the_bytes_decide()
    {
        Assert.True(HttpRunner.LooksTextual(null, "plain words"u8));
        Assert.False(HttpRunner.LooksTextual(null, new byte[] { 1, 0, 2 }));
    }

    [Fact]
    public void Decodes_with_the_declared_charset_or_bom()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal("Łódź", HttpRunner.Decode(Encoding.GetEncoding("windows-1250").GetBytes("Łódź"), "windows-1250"));
        Assert.Equal("hi", HttpRunner.Decode([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'], null));
        Assert.Equal("ok", HttpRunner.Decode("ok"u8.ToArray(), "no-such-charset"));
    }

    [Fact]
    public void Parses_set_cookie_headers()
    {
        var c = HttpRunner.ParseSetCookie("sid=a=b; Domain=.example.com; Path=/app; Expires=Wed, 21 Oct 2026 07:28:00 GMT; Secure; HttpOnly; SameSite=Strict");
        Assert.Equal(("sid", "a=b", ".example.com", "/app"), (c.Name, c.Value, c.Domain, c.Path));
        Assert.Equal("Wed, 21 Oct 2026 07:28:00 GMT", c.Expires);
        Assert.True(c.Secure && c.HttpOnly);
        Assert.Equal("Strict", c.SameSite);
        Assert.Null(HttpRunner.ParseSetCookie("x=1").Expires);
    }

    [Fact]
    public async Task Prepare_adds_defaults_auth_and_content_type_once()
    {
        var p = await HttpRunner.PrepareAsync(new HttpRequestModel
        {
            Method = "post",
            Url = "example.com/a",
            Headers = [new("accept", "application/json"), new("", "ignored")],
            Body = new RequestBody { Mode = "raw", RawType = "xml", Raw = "<a/>" },
            Auth = new RequestAuth { Type = "apikey", Key = "X-Key", Value = "v" },
        }, new Variables(null));
        Assert.Equal("POST", p.Method);
        Assert.Equal("http://example.com/a", p.Uri.ToString());
        Assert.Equal(["accept", "X-Key", "Content-Type", "User-Agent"], p.Headers.Select(h => h.Name));
        Assert.Equal("application/xml", p.Headers[2].Value);
        Assert.Equal("<a/>", Encoding.UTF8.GetString(p.Body!));
    }
}
