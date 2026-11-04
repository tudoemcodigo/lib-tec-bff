using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TEC.Bff.Theme;

/// <summary>Par de cores de um token: claro e escuro (<c>#rrggbb</c> em minúsculas).</summary>
internal sealed record ThemeColor(string Light, string Dark);

/// <summary>Tema sazonal como publicado no repositório de temas.</summary>
internal sealed record SeasonalTheme(
    string Id,
    DateOnly From,
    DateOnly To,
    IReadOnlyDictionary<string, ThemeColor> Tokens,
    string? Banner,
    string? Logo,
    string Animation);

/// <summary>Documento de temas validado (contrato 1).</summary>
internal sealed record ThemesDocument(IReadOnlyList<SeasonalTheme> Themes);

/// <summary>
/// Validação ESTRITA do documento de temas (FRT-TEM-01/07): as mesmas regras do pacote <c>@tudoemcodigo/theme</c> e do JSON Schema,
/// provadas pelos mesmos casos. Campo desconhecido, chave duplicada, tipo errado ou valor fora do formato recusa o documento
/// inteiro. As mensagens nunca repetem o valor recebido.
/// </summary>
internal static partial class ThemeDocumentParser
{
    public const int ContractVersion = 1;
    public const int MaxThemes = 64;
    public const int MaxJsonDepth = 8;

    /// <summary>Tokens que um tema pode trocar. Erro, aviso e sucesso são protegidos e não estão aqui.</summary>
    internal static readonly string[] TokenNames =
    [
        "color-bg", "color-surface", "color-text", "color-text-muted", "color-border", "color-border-subtle",
        "color-accent", "color-on-accent", "color-accent-subtle", "color-table-header", "color-row-hover",
        "shell-bg", "shell-hover", "shell-active", "shell-border", "shell-text", "shell-text-muted", "shell-accent"
    ];

    private static readonly string[] Animations = ["none", "snow"];

    // \A e \z (nunca ^ e $): no .NET o $ aceita uma quebra de linha final, e \d casa dígitos Unicode
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,63}\z")]
    internal static partial Regex IdPattern();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\A#[0-9a-fA-F]{6}\z")]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"\A(?!.*\.\.)[a-z0-9][a-z0-9._-]{0,63}\.(png|webp|avif)\z")]
    internal static partial Regex AssetPattern();

    public static bool TryParse(ReadOnlyMemory<byte> utf8, [NotNullWhen(true)] out ThemesDocument? document, out string error)
    {
        document = null;
        try
        {
            using var parsed = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = MaxJsonDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            return ParseRoot(parsed.RootElement, out document, out error);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            // Inclui UTF-8 inválido dentro de uma string: o System.Text.Json só o acusa ao ler o nome ou o valor, não ao fazer o parse
            document = null;
            error = "documento: JSON inválido";
            return false;
        }
    }

    private static bool ParseRoot(JsonElement root, out ThemesDocument? document, out string error)
    {
        document = null;
        if (!TryReadObject(root, ["contract", "themes"], out var fields))
            return Fail("documento: deve ser um objeto com somente contract e themes", out error);

        if (!fields.TryGetValue("contract", out var contract) || contract.ValueKind != JsonValueKind.Number
            || !contract.TryGetDouble(out double version) || version != ContractVersion)
            return Fail("documento: contract não suportado", out error);

        if (!fields.TryGetValue("themes", out var themes) || themes.ValueKind != JsonValueKind.Array)
            return Fail("themes: deve ser uma lista", out error);
        if (themes.GetArrayLength() > MaxThemes)
            return Fail("themes: temas demais", out error);

        var result = new List<SeasonalTheme>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var item in themes.EnumerateArray())
        {
            if (!TryParseTheme(item, out var theme, out string? why))
                return Fail($"themes[{index.ToString(CultureInfo.InvariantCulture)}]: {why}", out error);
            if (!ids.Add(theme.Id))
                return Fail($"themes[{index.ToString(CultureInfo.InvariantCulture)}]: id duplicado", out error);
            result.Add(theme);
            index++;
        }

        document = new ThemesDocument(result);
        error = string.Empty;
        return true;
    }

    private static bool TryParseTheme(JsonElement element, [NotNullWhen(true)] out SeasonalTheme? theme, [NotNullWhen(false)] out string? why)
    {
        theme = null;
        if (!TryReadObject(element, ["id", "from", "to", "tokens", "assets", "animation"], out var fields))
        {
            why = "deve ser um objeto com campos conhecidos";
            return false;
        }

        if (!TryString(fields, "id", out string? id) || !IdPattern().IsMatch(id))
        {
            why = "id inválido";
            return false;
        }

        if (!TryDate(fields, "from", out var from) || !TryDate(fields, "to", out var to))
        {
            why = "from e to devem ser datas AAAA-MM-DD reais";
            return false;
        }

        if (from > to)
        {
            why = "from não pode ser depois de to";
            return false;
        }

        if (!fields.TryGetValue("tokens", out var tokensElement) || !TryParseTokens(tokensElement, out var tokens))
        {
            why = "tokens inválidos";
            return false;
        }

        string? banner = null, logo = null;
        if (fields.TryGetValue("assets", out var assets) && !TryParseAssets(assets, out banner, out logo))
        {
            why = "assets inválidos";
            return false;
        }

        string animation = "none";
        if (fields.TryGetValue("animation", out var animationElement))
        {
            if (animationElement.ValueKind != JsonValueKind.String || animationElement.GetString() is not { } name || !Animations.Contains(name, StringComparer.Ordinal))
            {
                why = "animação desconhecida";
                return false;
            }

            animation = name;
        }

        theme = new SeasonalTheme(id, from, to, tokens, banner, logo, animation);
        why = null;
        return true;
    }

    private static bool TryParseTokens(JsonElement element, [NotNullWhen(true)] out Dictionary<string, ThemeColor>? tokens)
    {
        tokens = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var result = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // Nome desconhecido ou protegido, ou repetido: recusa o documento inteiro
            if (!TokenNames.Contains(property.Name, StringComparer.Ordinal) || result.ContainsKey(property.Name))
                return false;
            if (!TryReadObject(property.Value, ["light", "dark"], out var pair)
                || !TryString(pair, "light", out string? light) || !TryString(pair, "dark", out string? dark)
                || !ColorPattern().IsMatch(light) || !ColorPattern().IsMatch(dark))
                return false;
            result[property.Name] = new ThemeColor(light.ToLowerInvariant(), dark.ToLowerInvariant());
        }

        tokens = result;
        return true;
    }

    private static bool TryParseAssets(JsonElement element, out string? banner, out string? logo)
    {
        banner = logo = null;
        if (!TryReadObject(element, ["banner", "logo"], out var fields))
            return false;
        foreach (string key in new[] { "banner", "logo" })
        {
            if (!fields.TryGetValue(key, out var value))
                continue;
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } file || !AssetPattern().IsMatch(file))
                return false;
            if (key == "banner")
                banner = file;
            else
                logo = file;
        }

        return true;
    }

    private static bool TryDate(Dictionary<string, JsonElement> fields, string key, out DateOnly date)
    {
        date = default;
        return TryString(fields, key, out string? text) && DatePattern().IsMatch(text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryString(Dictionary<string, JsonElement> fields, string key, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!fields.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return value is not null;
    }

    /// <summary>Lê um objeto JSON recusando campo fora da lista e CHAVE DUPLICADA (o JSON.parse do navegador guardaria só a última).</summary>
    private static bool TryReadObject(JsonElement element, string[] allowed, out Dictionary<string, JsonElement> fields)
    {
        fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                return false;
        }

        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

/// <summary>Escolha do tema ativo pela data (FRT-TEM-06): janela inclusiva; vence o início mais recente e, no empate, o menor id.</summary>
internal static class ThemeSelector
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    public static SeasonalTheme? Select(ThemesDocument document, DateOnly today) =>
        document.Themes
            .Where(t => t.From <= today && today <= t.To)
            .OrderByDescending(t => t.From)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>Data de hoje em <c>America/Sao_Paulo</c> (sem horário de verão desde 2019: UTC−03:00 se o sistema não tiver o fuso).</summary>
    public static DateOnly Today(DateTimeOffset utcNow) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, Zone).DateTime);

    private static TimeZoneInfo ResolveZone()
    {
        foreach (string id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
                return zone;
        }

        return TimeZoneInfo.CreateCustomTimeZone("America/Sao_Paulo", TimeSpan.FromHours(-3), "America/Sao_Paulo", "America/Sao_Paulo");
    }
}
