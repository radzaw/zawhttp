using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ZawHTTP.Tests.Infrastructure;

/// <summary>A local HTTP server the runner tests send requests to: echoes requests, redirects, sets cookies, stalls.</summary>
public sealed class EchoServer : IAsyncDisposable
{
    readonly WebApplication app;
    public string Url { get; }

    EchoServer(WebApplication app, string url)
    {
        this.app = app;
        Url = url;
    }

    public static async Task<EchoServer> StartAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        // Everything about the request, as JSON.
        app.Map("/echo/{**rest}", async (HttpContext ctx) =>
        {
            var req = ctx.Request;
            string body;
            Dictionary<string, string>? form = null;
            Dictionary<string, string>? files = null;
            if (req.HasFormContentType)
            {
                var f = await req.ReadFormAsync();
                form = f.ToDictionary(x => x.Key, x => x.Value.ToString());
                files = new();
                foreach (var file in f.Files)
                {
                    using var r = new StreamReader(file.OpenReadStream());
                    files[file.Name] = $"{file.FileName}|{file.ContentType}|{await r.ReadToEndAsync()}";
                }
                body = "";
            }
            else
            {
                using var r = new StreamReader(req.Body, Encoding.UTF8);
                body = await r.ReadToEndAsync();
            }
            return Results.Json(new
            {
                method = req.Method,
                path = req.Path.Value,
                query = req.QueryString.Value,
                headers = req.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()),
                body,
                form,
                files,
            });
        });
        app.Map("/redirect/{code:int}", (int code, string? to, HttpContext ctx) =>
        {
            ctx.Response.StatusCode = code;
            ctx.Response.Headers.Location = to ?? "/echo/done";
            return Task.CompletedTask;
        });
        app.MapGet("/cookie/set", (string name, string value, HttpContext ctx) =>
        {
            ctx.Response.Headers.Append("Set-Cookie", $"{name}={value}; Path=/; HttpOnly; SameSite=Lax");
            return Results.Text("set");
        });
        app.MapGet("/slow", async (int ms, CancellationToken ct) =>
        {
            await Task.Delay(ms, ct);
            return Results.Text("late");
        });
        app.MapGet("/latin2", () => Results.Bytes(Encoding.GetEncoding("iso-8859-2").GetBytes("Zażółć gęślą jaźń"), "text/plain; charset=iso-8859-2"));
        app.MapGet("/binary", () => Results.Bytes([0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3], "image/png"));
        app.MapGet("/status/{code:int}", (int code) => Results.Text("status " + code, statusCode: code));
        app.MapGet("/download", (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"report.csv\"";
            return Results.Text("a,b\n1,2", "text/csv");
        });

        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new EchoServer(app, url);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
