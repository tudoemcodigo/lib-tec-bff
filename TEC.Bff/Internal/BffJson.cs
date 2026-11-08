using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using TEC.Core.Common.Serialization;
using TEC.Core.Responses;

namespace TEC.Bff.Internal;

/// <summary>Corpo de <c>GET /bff/user</c>. Nunca contém token nem identificador de sessão.</summary>
internal sealed record UserResponse(string? Name, IReadOnlyList<string> Permissions);

/// <summary>Resposta do endpoint de token do provedor (renovação). O <c>ToString()</c> não revela os tokens.</summary>
internal sealed class TokenEndpointResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public JsonElement ExpiresIn { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    public override string ToString() => "TokenEndpointResponse { redigido }";
}

/// <summary>Corpo de <c>POST /bff/login/password</c>. O <c>ToString()</c> não revela a senha.</summary>
internal sealed class PasswordLoginRequest
{
    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? ReturnUrl { get; init; }

    public override string ToString() => "PasswordLoginRequest { redigido }";
}

/// <summary>Resposta do login por senha aceito: para onde a SPA navega (caminho local já conferido).</summary>
internal sealed record PasswordLoginResponse(string ReturnUrl);

/// <summary>Metadados JSON gerados em compilação (compatível com Native AOT).</summary>
[JsonSerializable(typeof(ApiResponse))]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(TokenEndpointResponse))]
[JsonSerializable(typeof(PasswordLoginRequest))]
[JsonSerializable(typeof(PasswordLoginResponse))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class BffJsonContext : JsonSerializerContext;

/// <summary>Respostas de erro do BFF no envelope <see cref="ApiResponse"/>, sem detalhe interno.</summary>
internal static class BffResponses
{
    public const string Unauthenticated = "BFF_NAO_AUTENTICADO";
    public const string Csrf = "BFF_CSRF";
    public const string BodyTooLarge = "BFF_CORPO_GRANDE";
    public const string Unavailable = "BFF_INDISPONIVEL";
    public const string Upstream = "BFF_ERRO_UPSTREAM";
    public const string NotFound = "BFF_NAO_ENCONTRADO";
    public const string InvalidRequest = "BFF_REQUISICAO_INVALIDA";
    public const string UnsupportedMediaType = "BFF_TIPO_NAO_SUPORTADO";
    public const string MethodNotAllowed = "BFF_METODO_NAO_PERMITIDO";
    public const string InvalidCredentials = "BFF_CREDENCIAIS_INVALIDAS";
    public const string TooManyAttempts = "BFF_TENTATIVAS_DEMAIS";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        var response = context.Response;
        if (response.HasStarted)
            return Task.CompletedTask;

        response.StatusCode = status;
        response.Headers.CacheControl = "no-store";
        var body = ApiResponse.Fail(status, message, new ApiError(code, message)) with
        {
            TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
        };
        var typeInfo = (JsonTypeInfo<ApiResponse>)Options.GetTypeInfo(typeof(ApiResponse));
        return response.WriteAsJsonAsync(body, typeInfo, "application/json; charset=utf-8", context.RequestAborted);
    }

    public static Task WriteUnauthenticatedAsync(HttpContext context) =>
        WriteErrorAsync(context, StatusCodes.Status401Unauthorized, Unauthenticated, "Autenticação necessária.");

    /// <summary>Serializa com as mesmas opções do envelope (camelCase), não com as opções cruas do contexto gerado.</summary>
    public static Task WriteJsonAsync<T>(HttpContext context, T value)
    {
        context.Response.Headers.CacheControl = "no-store";
        var typeInfo = (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
        return context.Response.WriteAsJsonAsync(value, typeInfo, "application/json; charset=utf-8", context.RequestAborted);
    }

    internal static JsonSerializerOptions JsonOptions => Options;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = JsonDefaults.CreateOptions(BffJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
