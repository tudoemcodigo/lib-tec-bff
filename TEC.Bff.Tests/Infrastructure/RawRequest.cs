using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;

namespace TEC.Bff.Tests.Infrastructure;

/// <summary>
/// Requisição com o caminho exatamente como o atacante o escreveria. O <c>HttpClient</c> normaliza <c>..</c> e <c>%2e%2e</c> antes de
/// enviar; o <c>TestServer</c> aceita o caminho cru, que é o que um cliente malicioso (curl --path-as-is) mandaria ao Kestrel.
/// </summary>
internal static class RawRequest
{
    public static async Task<(int Status, string Body, IHeaderDictionary Headers)> SendAsync(BffApp app, Browser? browser, string method, string path, string? rawTarget = null,
        Action<HttpContext>? configure = null)
    {
        var context = await app.App.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Scheme = "https";
            c.Request.Host = new HostString("localhost");
            c.Request.Path = new PathString(path);
            c.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget = rawTarget ?? path;
            c.Request.Headers["Accept"] = "application/json";
            c.Request.Headers["Sec-Fetch-Site"] = "same-origin";
            if (browser is { Cookies.Count: > 0 })
                c.Request.Headers.Cookie = string.Join("; ", browser.Cookies.Select(x => $"{x.Key}={x.Value}"));
            configure?.Invoke(c);
        }).ConfigureAwait(false);

        using var reader = new StreamReader(context.Response.Body);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);
        return (context.Response.StatusCode, body, context.Response.Headers);
    }
}
