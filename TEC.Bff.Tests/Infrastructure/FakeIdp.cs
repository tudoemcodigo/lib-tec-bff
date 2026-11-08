using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TEC.Bff.Tests.Infrastructure;

/// <summary>
/// Provedor OpenID Connect em memória (descoberta, JWKS e endpoint de token) usado como <c>HttpMessageHandler</c> do login e da
/// renovação. Os access e refresh tokens são textos reconhecíveis (<c>ACCESS-n</c>, <c>REFRESH-n</c>) para que os testes de
/// vazamento os procurem em respostas, cookies e logs.
/// </summary>
internal sealed class FakeIdp : HttpMessageHandler
{
    public const string Authority = "https://idp.test/realms/corp";
    public const string ClientId = "bff-client";
    public const string ClientSecret = "client-secret-value-123";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly JsonWebTokenHandler _jwt = new();
    private int _sequence;
    private int _refreshCalls;
    private int _codeRedemptions;

    public FakeIdp() => _key = new RsaSecurityKey(_rsa) { KeyId = "k1" };

    public string? PendingNonce { get; set; }

    /// <summary>Nonce por código de autorização (logins em paralelo não se atropelam).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> NonceByCode { get; } = new();

    public string Subject { get; set; } = "user-1";

    public string Name { get; set; } = "Maria Teste";

    public string[] Roles { get; set; } = ["clientes.ler", "pedidos.ler"];

    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Resposta do endpoint de token sem <c>access_token</c> (login deve falhar fechado).</summary>
    public bool OmitAccessToken { get; set; }

    /// <summary>Refresh token ausente na troca do código (sessão sem renovação).</summary>
    public bool OmitRefreshToken { get; set; }

    /// <summary>Substitui a resposta da renovação (falha, lentidão, corpo inválido).</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? RefreshBehavior { get; set; }

    public int RefreshCalls => Volatile.Read(ref _refreshCalls);

    public int CodeRedemptions => Volatile.Read(ref _codeRedemptions);

    public List<string> ReceivedClientSecrets { get; } = [];

    public List<string> ReceivedRefreshTokens { get; } = [];

    public string? LastCodeVerifier { get; private set; }

    /// <summary>Anuncia o endpoint de PAR na descoberta (como o Keycloak) e o atende.</summary>
    public bool SupportsPar { get; set; }

    /// <summary>Corpos recebidos no endpoint de PAR.</summary>
    public List<string> PushedRequests { get; } = [];

    public const string PasswordUser = "maria";
    public const string PasswordValue = "senha-correta-123";

    private int _passwordCalls;

    /// <summary>Pedidos de <c>grant_type=password</c> que chegaram ao IdP (o limite do BFF corta antes de chegar).</summary>
    public int PasswordCalls => Volatile.Read(ref _passwordCalls);

    /// <summary>Substitui a resposta do fluxo de senha (indisponível, sem id_token).</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? PasswordBehavior { get; set; }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            return Json(Discovery());
        if (path.EndsWith("/protocol/openid-connect/certs", StringComparison.Ordinal))
            return Json(Jwks());
        if (path.EndsWith("/protocol/openid-connect/ext/par/request", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            string pushed = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (PushedRequests)
                PushedRequests.Add(pushed);
            return Json("{\"request_uri\":\"urn:ietf:params:oauth:request_uri:abc\",\"expires_in\":60}", HttpStatusCode.Created);
        }
        if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            return await TokenAsync(request, cancellationToken).ConfigureAwait(false);

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string form = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var values = System.Web.HttpUtility.ParseQueryString(form);
        lock (ReceivedClientSecrets)
        {
            if (values["client_secret"] is { } secret)
                ReceivedClientSecrets.Add(secret);
        }

        if (values["grant_type"] == "authorization_code")
        {
            Interlocked.Increment(ref _codeRedemptions);
            LastCodeVerifier = values["code_verifier"];
            if (string.IsNullOrEmpty(LastCodeVerifier))
                return Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest);

            string? nonce = values["code"] is { } code && NonceByCode.TryGetValue(code, out string? byCode) ? byCode : PendingNonce;
            return Json(TokenBody(includeIdToken: true, nonce));
        }

        if (values["grant_type"] == "refresh_token")
        {
            Interlocked.Increment(ref _refreshCalls);
            lock (ReceivedRefreshTokens)
                ReceivedRefreshTokens.Add(values["refresh_token"] ?? string.Empty);

            if (RefreshBehavior is { } behavior)
                return await behavior(request, cancellationToken).ConfigureAwait(false);

            return Json(TokenBody(includeIdToken: false));
        }

        if (values["grant_type"] == "password")
        {
            Interlocked.Increment(ref _passwordCalls);
            if (PasswordBehavior is { } passwordBehavior)
                return await passwordBehavior(request, cancellationToken).ConfigureAwait(false);
            // Como o Keycloak: usuário inexistente e senha errada dão a mesma resposta (invalid_grant)
            if (values["username"] != PasswordUser || values["password"] != PasswordValue || values["client_secret"] is null)
                return Json("{\"error\":\"invalid_grant\",\"error_description\":\"Invalid user credentials\"}", HttpStatusCode.BadRequest);

            return Json(TokenBody(includeIdToken: true));
        }

        return Json("{\"error\":\"unsupported_grant_type\"}", HttpStatusCode.BadRequest);
    }

    /// <summary>Corpo de uma resposta de token bem-sucedida (usado também pelos comportamentos de renovação dos testes).</summary>
    public string TokenBody(bool includeIdToken, string? nonce = null)
    {
        int n = Interlocked.Increment(ref _sequence);
        var parts = new List<string>();
        if (!OmitAccessToken)
            parts.Add($"\"access_token\":\"ACCESS-{n}\"");
        if (!OmitRefreshToken)
            parts.Add($"\"refresh_token\":\"REFRESH-{n}\"");
        parts.Add("\"token_type\":\"Bearer\"");
        parts.Add($"\"expires_in\":{((int)AccessLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture)}");
        if (includeIdToken)
            parts.Add($"\"id_token\":\"{IdToken(nonce)}\"");
        return "{" + string.Join(',', parts) + "}";
    }

    private string IdToken(string? nonce)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Subject,
            ["name"] = Name,
            ["roles"] = Roles,
            ["sid"] = "idp-session-1",
            ["auth_time"] = new DateTimeOffset(now).ToUnixTimeSeconds()
        };
        if (nonce is not null)
            claims["nonce"] = nonce;

        return _jwt.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = ClientId,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256)
        });
    }

    private string Discovery() => JsonSerializer.Serialize(SupportsPar ? WithPar(DiscoveryValues()) : DiscoveryValues());

    private static Dictionary<string, object> WithPar(Dictionary<string, object> values)
    {
        values["pushed_authorization_request_endpoint"] = Authority + "/protocol/openid-connect/ext/par/request";
        return values;
    }

    private static Dictionary<string, object> DiscoveryValues() => new()
    {
        ["issuer"] = Authority,
        ["authorization_endpoint"] = Authority + "/protocol/openid-connect/auth",
        ["token_endpoint"] = Authority + "/protocol/openid-connect/token",
        ["jwks_uri"] = Authority + "/protocol/openid-connect/certs",
        ["end_session_endpoint"] = Authority + "/protocol/openid-connect/logout",
        ["response_types_supported"] = new[] { "code" },
        ["subject_types_supported"] = new[] { "public" },
        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        ["code_challenge_methods_supported"] = new[] { "S256" }
    };

    private string Jwks()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(includePrivateParameters: false)) { KeyId = "k1" });
        return JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _rsa.Dispose();
        base.Dispose(disposing);
    }
}
