using System.Net;
using System.Text;
using System.Text.Json;
using ZawHTTP.Tests.Infrastructure;

namespace ZawHTTP.Tests.Integration;

public class AppHostTests
{
    [Fact]
    public async Task Api_calls_need_the_token_but_the_ui_does_not()
    {
        await using var app = await TestApp.StartAsync();
        using var anon = new HttpClient { BaseAddress = new Uri(app.BaseAddress) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/version")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/version?token=wrong-token-0000000")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/version?token={TestApp.Token}")).StatusCode);

        var index = await anon.GetStringAsync("/");
        Assert.Contains("<title>ZawHTTP</title>", index);
        Assert.Contains("export", await anon.GetStringAsync("/js/app.js"));
        app.DeleteConfig();
    }

    [Fact]
    public async Task Documents_are_saved_and_survive_a_restart()
    {
        var app = await TestApp.StartAsync();
        Assert.Equal(JsonValueKind.Null, (await app.GetAsync("/doc/collections")).Expect().ValueKind);

        var doc = """{"collections":[{"id":"c1","type":"collection","name":"Mine","items":[]}]}""";
        using (var put = await app.Http.PutAsync("/api/doc/collections", new StringContent(doc, Encoding.UTF8, "application/json")))
            put.EnsureSuccessStatusCode();
        using (var put = await app.Http.PutAsync("/api/doc/state", new StringContent("""{"prefs":{"theme":"dark"}}""", Encoding.UTF8, "application/json")))
            put.EnsureSuccessStatusCode();
        var bad = await app.CallAsync(HttpMethod.Get, "/doc/secrets");
        Assert.False(bad.Ok);
        await app.DisposeAsync();

        await using var again = await TestApp.StartAsync(app.ConfigDir);
        var cols = (await again.GetAsync("/doc/collections")).Expect();
        Assert.Equal("Mine", cols.GetProperty("collections")[0].GetProperty("name").GetString());
        Assert.Equal("dark", (await again.GetAsync("/doc/state")).Expect().GetProperty("prefs").GetProperty("theme").GetString());
        again.DeleteConfig();
    }

    [Fact]
    public void A_damaged_document_is_set_aside_instead_of_failing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zawhttp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "environments.json"), "{ not json");
        var ws = new Workspace(AppOptions.Parse(["--config", dir]));
        Assert.Equal(JsonValueKind.Undefined, ws.Load("environments").ValueKind);
        Assert.True(File.Exists(Path.Combine(dir, "environments.json.broken")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Options_are_parsed_and_weak_tokens_rejected()
    {
        var o = AppOptions.Parse(["--port", "5000", "--no-browser", "--token", "abcdefghijklmnop-_"]);
        Assert.Equal(5000, o.Port);
        Assert.True(o.NoBrowser);
        Assert.True(o.KeepAlive);
        Assert.Equal("abcdefghijklmnop-_", o.Token);
        Assert.Throws<ArgumentException>(() => AppOptions.Parse(["--token", "short"]));
        Assert.Throws<ArgumentException>(() => AppOptions.Parse(["--token", "has spaces in the token"]));
        Assert.Equal(40, AppOptions.Parse([]).Token.Length);
    }

    [Fact]
    public void History_is_capped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zawhttp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var ws = new Workspace(AppOptions.Parse(["--config", dir]));
        for (var i = 0; i < Workspace.MaxHistory + 20; i++)
            ws.AddHistory(new HistoryEntry(i.ToString(), i, "GET", "http://x/" + i, 200, 1, null, null, new HttpRequestModel()));
        var h = new Workspace(AppOptions.Parse(["--config", dir])).History();
        Assert.Equal(Workspace.MaxHistory, h.Count);
        Assert.Equal((Workspace.MaxHistory + 19).ToString(), h[0].Id);
        Directory.Delete(dir, true);
    }
}
