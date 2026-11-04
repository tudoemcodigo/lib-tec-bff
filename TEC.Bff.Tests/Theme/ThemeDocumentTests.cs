using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TEC.Bff.Theme;

namespace TEC.Bff.Tests.Theme;

/// <summary>
/// Validação do documento de temas (FRT-TEM-01/06/07). Os casos de <c>themes-cases.json</c> são os MESMOS do pacote
/// <c>@tudoemcodigo/theme</c>: TypeScript e C# têm de concordar em tudo.
/// </summary>
public class ThemeDocumentTests
{
    /// <summary>SHA-256 (fim de linha LF) de <c>Fixtures/themes-cases.json</c>. Mudou o arquivo? Atualize a cópia em lib-tec-web e este valor juntos.</summary>
    private const string CasesSha256 = "d5bcbdc836f4b7e03a3c71809d4ce578aa9fee1929b9f672cdd3e66a85f00b24";

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Theme", "Fixtures", name);

    private static JsonElement Cases() => JsonDocument.Parse(File.ReadAllText(FixturePath("themes-cases.json"))).RootElement.Clone();

    private static bool Parses(string json, out string error) =>
        ThemeDocumentParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out error);

    [Test]
    public async Task The_shared_cases_file_has_not_drifted_from_the_package_copy()
    {
        string normalized = File.ReadAllText(FixturePath("themes-cases.json")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

        await Assert.That(hash).IsEqualTo(CasesSha256);
    }

    [Test]
    public async Task The_token_allowlist_matches_the_json_schema()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(FixturePath("themes.schema.json")));
        var listed = schema.RootElement.GetProperty("$defs").GetProperty("theme").GetProperty("properties")
            .GetProperty("tokens").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

        await Assert.That(listed).IsEquivalentTo(ThemeDocumentParser.TokenNames);
    }

    [Test]
    public async Task Every_valid_shared_case_is_accepted()
    {
        var rejected = new List<string>();
        foreach (var item in Cases().GetProperty("validDocuments").EnumerateArray())
        {
            if (!Parses(item.GetProperty("document").GetRawText(), out _))
                rejected.Add(item.GetProperty("name").GetString()!);
        }

        await Assert.That(rejected).IsEmpty();
    }

    [Test]
    public async Task Every_hostile_shared_case_is_refused()
    {
        var accepted = new List<string>();
        foreach (var item in Cases().GetProperty("invalidDocuments").EnumerateArray())
        {
            if (Parses(item.GetProperty("document").GetRawText(), out _))
                accepted.Add(item.GetProperty("name").GetString()!);
        }

        await Assert.That(accepted).IsEmpty();
    }

    [Test]
    public async Task The_shared_selection_cases_agree_with_the_typescript_validator()
    {
        var wrong = new List<string>();
        foreach (var item in Cases().GetProperty("selection").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            if (!ThemeDocumentParser.TryParse(Encoding.UTF8.GetBytes(item.GetProperty("document").GetRawText()), out var document, out _))
            {
                wrong.Add(name + " (documento recusado)");
                continue;
            }

            // Data inválida no caso compartilhado: o C# recebe DateOnly, então o caso vale só quando a data existe
            if (!DateOnly.TryParseExact(item.GetProperty("today").GetString(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var today))
            {
                continue;
            }

            string? expected = item.GetProperty("expectedId").ValueKind == JsonValueKind.Null ? null : item.GetProperty("expectedId").GetString();
            if (ThemeSelector.Select(document, today)?.Id != expected)
                wrong.Add(name);
        }

        await Assert.That(wrong).IsEmpty();
    }

    // --- Diferenças entre parsers: o que o JSON.parse do navegador esconderia, o C# recusa ---

    [Test]
    public async Task Duplicate_keys_are_refused()
    {
        const string DuplicateId = """{"contract":1,"themes":[{"id":"a","id":"b","from":"2026-12-01","to":"2026-12-02","tokens":{}}]}""";
        const string DuplicateToken = """{"contract":1,"themes":[{"id":"a","from":"2026-12-01","to":"2026-12-02","tokens":{"color-accent":{"light":"#000000","dark":"#ffffff"},"color-accent":{"light":"#111111","dark":"#eeeeee"}}}]}""";
        const string DuplicateRoot = """{"contract":1,"contract":1,"themes":[]}""";

        await Assert.That(Parses(DuplicateId, out _)).IsFalse();
        await Assert.That(Parses(DuplicateToken, out _)).IsFalse();
        await Assert.That(Parses(DuplicateRoot, out _)).IsFalse();
    }

    [Test]
    public async Task A_trailing_newline_does_not_sneak_past_the_patterns()
    {
        // No .NET o $ aceitaria "a\n"; os padrões usam \z
        await Assert.That(Parses("""{"contract":1,"themes":[{"id":"a\n","from":"2026-12-01","to":"2026-12-02","tokens":{}}]}""", out _)).IsFalse();
        await Assert.That(Parses("""{"contract":1,"themes":[{"id":"a","from":"2026-12-01\n","to":"2026-12-02","tokens":{}}]}""", out _)).IsFalse();
        await Assert.That(Parses("""{"contract":1,"themes":[{"id":"a","from":"2026-12-01","to":"2026-12-02","tokens":{"color-accent":{"light":"#000000\n","dark":"#ffffff"}}}]}""", out _)).IsFalse();
        await Assert.That(Parses("""{"contract":1,"themes":[{"id":"a","from":"2026-12-01","to":"2026-12-02","tokens":{},"assets":{"logo":"logo.png\n"}}]}""", out _)).IsFalse();
    }

    [Test]
    public async Task Unicode_digits_are_not_dates()
    {
        // \d no .NET casaria dígitos de outros alfabetos
        await Assert.That(Parses("""{"contract":1,"themes":[{"id":"a","from":"٢٠٢٦-١٢-٠١","to":"2026-12-02","tokens":{}}]}""", out _)).IsFalse();
    }

    [Test]
    public async Task Contract_one_point_zero_is_accepted_like_the_browser_would()
    {
        await Assert.That(Parses("""{"contract":1.0,"themes":[]}""", out _)).IsTrue();
        await Assert.That(Parses("""{"contract":1e0,"themes":[]}""", out _)).IsTrue();
        await Assert.That(Parses("""{"contract":"1","themes":[]}""", out _)).IsFalse();
        await Assert.That(Parses("""{"contract":true,"themes":[]}""", out _)).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("{")]
    [Arguments("[]")]
    [Arguments("null")]
    [Arguments("\"texto\"")]
    [Arguments("{\"contract\":1,\"themes\":[],}")]
    [Arguments("{\"contract\":1,/*c*/\"themes\":[]}")]
    [Arguments("{\"contract\":1,\"themes\":[] } {\"x\":1}")]
    public async Task Malformed_json_is_refused(string json)
    {
        await Assert.That(Parses(json, out string error)).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task Invalid_utf8_is_refused_without_throwing()
    {
        byte[] bytes = [0x7B, 0x22, 0xFF, 0xFE, 0x22, 0x3A, 0x31, 0x7D];

        await Assert.That(ThemeDocumentParser.TryParse(bytes, out var document, out _)).IsFalse();
        await Assert.That(document).IsNull();
    }

    [Test]
    public async Task A_deeply_nested_document_is_refused_before_it_costs_anything()
    {
        string deep = string.Concat(Enumerable.Repeat("[", 200)) + string.Concat(Enumerable.Repeat("]", 200));

        await Assert.That(Parses("{\"contract\":1,\"themes\":" + deep + "}", out _)).IsFalse();
    }

    [Test]
    public async Task The_error_never_echoes_the_hostile_value()
    {
        Parses("""{"contract":1,"themes":[{"id":"<script>alert(1)</script>","from":"2026-12-01","to":"2026-12-02","tokens":{}}]}""", out string error);

        await Assert.That(error).DoesNotContain("script");
    }

    [Test]
    public async Task Colors_are_normalized_to_lowercase()
    {
        ThemeDocumentParser.TryParse("""{"contract":1,"themes":[{"id":"a","from":"2026-12-01","to":"2026-12-02","tokens":{"color-accent":{"light":"#B3001B","dark":"#FF9AA6"}}}]}"""u8.ToArray(), out var document, out _);

        await Assert.That(document!.Themes[0].Tokens["color-accent"]).IsEqualTo(new ThemeColor("#b3001b", "#ff9aa6"));
    }

    [Test]
    public async Task The_theme_limit_is_enforced()
    {
        string Build(int count) => "{\"contract\":1,\"themes\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => $"{{\"id\":\"t{i}\",\"from\":\"2026-01-01\",\"to\":\"2026-01-02\",\"tokens\":{{}}}}")) + "]}";

        await Assert.That(Parses(Build(ThemeDocumentParser.MaxThemes), out _)).IsTrue();
        await Assert.That(Parses(Build(ThemeDocumentParser.MaxThemes + 1), out _)).IsFalse();
    }

    // --- Fuso: a data é a de America/Sao_Paulo, não a UTC ---

    [Test]
    public async Task Today_follows_the_sao_paulo_calendar_not_utc()
    {
        // 02:59:59Z ainda é 30/11 em São Paulo (UTC−03); 03:00:00Z já é 01/12
        await Assert.That(ThemeSelector.Today(new DateTimeOffset(2026, 12, 1, 2, 59, 59, TimeSpan.Zero))).IsEqualTo(new DateOnly(2026, 11, 30));
        await Assert.That(ThemeSelector.Today(new DateTimeOffset(2026, 12, 1, 3, 0, 0, TimeSpan.Zero))).IsEqualTo(new DateOnly(2026, 12, 1));
        await Assert.That(ThemeSelector.Today(new DateTimeOffset(2026, 12, 27, 2, 59, 59, TimeSpan.Zero))).IsEqualTo(new DateOnly(2026, 12, 26));
    }
}
