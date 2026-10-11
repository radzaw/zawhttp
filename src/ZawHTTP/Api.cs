using System.Text.Json;

namespace ZawHTTP;

/// <summary>An error with a message meant for the user.</summary>
public sealed class ApiException(string message) : Exception(message);

public static class Api
{
    sealed record Response(bool Ok, object? Data, string? Error);

    static async Task<IResult> Run(Func<Task<object?>> body)
    {
        try
        {
            return Results.Json(new Response(true, await body(), null), Workspace.Json);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(new Response(false, null, "The operation was cancelled."), Workspace.Json);
        }
        catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        {
            return Results.Json(new Response(false, null, ex.Message), Workspace.Json);
        }
    }

    static Task<IResult> RunSync(Func<object?> body) => Run(() => Task.FromResult(body()));

    static string NewId() => Guid.NewGuid().ToString("n");

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- application ----
        api.MapPost("/ping", (string? page, Heartbeat hb) => { hb.Ping(page); return Results.Ok(); });
        api.MapPost("/bye", (string? page, Heartbeat hb) => { hb.Bye(page); return Results.Ok(); });
        api.MapPost("/exit", (IHostApplicationLifetime life) =>
        {
            _ = Task.Delay(300).ContinueWith(_ => life.StopApplication());
            return RunSync(() => null);
        });
        api.MapGet("/version", () => RunSync(() => new { version = HttpRunner.Version, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, runtime = Environment.Version.ToString() }));

        // ---- workspace documents: state, collections, environments ----
        api.MapGet("/doc/{name}", (string name, Workspace ws) => RunSync(() =>
        {
            if (!Workspace.IsDocument(name)) throw new ApiException($"Unknown document \"{name}\".");
            var d = ws.Load(name);
            return d.ValueKind == JsonValueKind.Undefined ? null : d;
        }));
        api.MapPut("/doc/{name}", async (string name, HttpRequest req, Workspace ws) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement.Clone();
            return await RunSync(() =>
            {
                if (!Workspace.IsDocument(name)) throw new ApiException($"Unknown document \"{name}\".");
                ws.Save(name, root);
                return null;
            });
        });

        // ---- history ----
        api.MapGet("/history", (Workspace ws) => RunSync(() => ws.History()));
        api.MapDelete("/history", (Workspace ws) => RunSync(() => { ws.DeleteHistory(null); return null; }));
        api.MapDelete("/history/{id}", (string id, Workspace ws) => RunSync(() => { ws.DeleteHistory(id); return null; }));

        // ---- sending ----
        api.MapPost("/send", (SendRequest r, HttpRunner runner, Workspace ws, HttpContext ctx) => Run(async () =>
        {
            var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                var res = await runner.SendAsync(r, ctx.RequestAborted);
                ws.AddHistory(new HistoryEntry(NewId(), started, r.Request.Method, r.Request.Url, res.Status, res.TotalMs, null, r.Name, r.Request));
                return res;
            }
            catch (ApiException ex)
            {
                ws.AddHistory(new HistoryEntry(NewId(), started, r.Request.Method, r.Request.Url, null, null, ex.Message, r.Name, r.Request));
                throw;
            }
        }));
        api.MapPost("/cancel", (CancelRequest r, HttpRunner runner) => RunSync(() => new { cancelled = runner.Cancel(r.RunId) }));

        // Raw response bytes, for image / PDF previews and "Save response".
        api.MapGet("/response/{id}", (string id, bool? download, HttpRunner runner) =>
        {
            var b = runner.Body(id);
            if (b == null) return Results.NotFound();
            return Results.File(b.Bytes, b.ContentType ?? "application/octet-stream", download == true ? b.FileName : null);
        });

        // ---- cookie jar ----
        api.MapGet("/cookies", (HttpRunner runner) => RunSync(() => runner.Cookies()));
        api.MapDelete("/cookies", (string? domain, string? name, string? path, HttpRunner runner) =>
            RunSync(() => new { deleted = runner.DeleteCookies(domain, name, path) }));

        // ---- import / export ----
        api.MapPost("/import", (ImportRequest r) => RunSync(() => Importer.Import(r.Text ?? "")));
        api.MapPost("/import/file", (ImportFileRequest r) => RunSync(() =>
        {
            var path = r.Path.Trim().Trim('"');
            if (!File.Exists(path)) throw new ApiException($"File not found: {path}");
            return Importer.Import(File.ReadAllText(path));
        }));
        api.MapPost("/export/curl", (CurlExportRequest r) => RunSync(() => new { text = Curl.Export(r.Request, r.Variables) }));
    }
}

public sealed record ImportFileRequest(string Path);
