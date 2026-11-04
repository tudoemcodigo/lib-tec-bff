using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TEC.Bff.Internal;

namespace TEC.Bff.Theme;

/// <summary>
/// <c>GET /bff/theme</c> e <c>GET /bff/theme/assets/{id}/{arquivo}</c>. Públicos (o tema não tem dado pessoal), só leitura, sem entrada do
/// usuário além de id e arquivo validados por padrão fechado. Qualquer falha é "sem tema": a SPA fica no tema padrão.
/// </summary>
internal static class ThemeEndpoints
{
    public const string AssetsPath = BffOptions.BasePath + "/theme/assets";

    public static async Task HandleThemeAsync(HttpContext context, ThemeStore store)
    {
        var theme = await store.GetActiveThemeAsync(context.RequestAborted).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contract", ThemeDocumentParser.ContractVersion);
            if (theme is null)
            {
                writer.WriteNull("theme");
            }
            else
            {
                writer.WritePropertyName("theme");
                WriteTheme(writer, theme);
            }

            writer.WriteEndObject();
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), context.RequestAborted).ConfigureAwait(false);
    }

    public static async Task HandleAssetAsync(HttpContext context, ThemeStore store)
    {
        string id = context.Request.RouteValues["id"] as string ?? string.Empty;
        string file = context.Request.RouteValues["file"] as string ?? string.Empty;

        // O caminho cru também: %2e, %2f e %5c não têm uso legítimo aqui
        string raw = context.Request.Path.Value ?? string.Empty;
        var asset = raw.Contains('%', StringComparison.Ordinal) ? null : await store.GetAssetAsync(id, file, context.RequestAborted).ConfigureAwait(false);
        if (asset is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = asset.ContentType;
        context.Response.Headers.CacheControl = "public, max-age=300";
        context.Response.ContentLength = asset.Content.Length;
        await context.Response.Body.WriteAsync(asset.Content, context.RequestAborted).ConfigureAwait(false);
    }

    private static void WriteTheme(Utf8JsonWriter writer, SeasonalTheme theme)
    {
        writer.WriteStartObject();
        writer.WriteString("id", theme.Id);

        writer.WritePropertyName("tokens");
        writer.WriteStartObject();
        foreach (var (name, color) in theme.Tokens.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(name);
            writer.WriteStartObject();
            writer.WriteString("light", color.Light);
            writer.WriteString("dark", color.Dark);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        writer.WritePropertyName("assets");
        writer.WriteStartObject();
        if (theme.Banner is not null)
            writer.WriteString("banner", $"{AssetsPath}/{theme.Id}/{theme.Banner}");
        if (theme.Logo is not null)
            writer.WriteString("logo", $"{AssetsPath}/{theme.Id}/{theme.Logo}");
        writer.WriteEndObject();

        writer.WriteString("animation", theme.Animation);
        writer.WriteEndObject();
    }
}
