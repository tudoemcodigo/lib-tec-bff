using Microsoft.Extensions.Options;
using TEC.Bff.Internal;
using TEC.Bff.Sessions;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>Armazém de sessões em memória: teto, expiração, atualização e concorrência.</summary>
public class SessionStoreTests
{
    private static InMemoryBffSessionStore Store(int max, ManualTimeProvider? clock = null) =>
        new(Options.Create(new BffOptions { MaxSessions = max }), clock ?? new ManualTimeProvider());

    private static BffTokenSet Tokens(string access = "A", TimeSpan? sessionFor = null) =>
        new(access, "R", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.Add(sessionFor ?? TimeSpan.FromHours(8)));

    [Test]
    public async Task Add_get_update_and_remove_work()
    {
        var store = Store(10);

        await Assert.That(await store.TryAddAsync("s1", Tokens("A1"), default)).IsTrue();
        await Assert.That((await store.GetAsync("s1", default))!.AccessToken).IsEqualTo("A1");
        await Assert.That(await store.TryUpdateAsync("s1", Tokens("A2"), default)).IsTrue();
        await Assert.That((await store.GetAsync("s1", default))!.AccessToken).IsEqualTo("A2");
        await store.RemoveAsync("s1", default);
        await store.RemoveAsync("s1", default);   // idempotente
        await Assert.That(await store.GetAsync("s1", default)).IsNull();
        await Assert.That(await store.TryUpdateAsync("s1", Tokens(), default)).IsFalse();   // não ressuscita sessão apagada
    }

    [Test]
    public async Task Duplicate_id_and_full_store_fail_closed()
    {
        var store = Store(2);

        await Assert.That(await store.TryAddAsync("a", Tokens(), default)).IsTrue();
        await Assert.That(await store.TryAddAsync("a", Tokens(), default)).IsFalse();   // não sobrescreve a sessão de outro
        await Assert.That(await store.TryAddAsync("b", Tokens(), default)).IsTrue();
        await Assert.That(await store.TryAddAsync("c", Tokens(), default)).IsFalse();
        await Assert.That(store.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Expired_sessions_are_not_returned_and_free_room_when_full()
    {
        var clock = new ManualTimeProvider();
        var store = Store(2, clock);
        await store.TryAddAsync("velha", Tokens(sessionFor: TimeSpan.FromMinutes(10)), default);
        await store.TryAddAsync("nova", Tokens(sessionFor: TimeSpan.FromHours(5)), default);

        clock.Advance(TimeSpan.FromMinutes(11));

        await Assert.That(await store.GetAsync("velha", default)).IsNull();
        await Assert.That(await store.TryAddAsync("outra", Tokens(), default)).IsTrue();   // o espaço da vencida é reaproveitado
        await Assert.That(await store.GetAsync("nova", default)).IsNotNull();
    }

    [Test]
    public async Task Concurrent_adds_never_exceed_the_cap()
    {
        var store = Store(100);

        var results = await Task.WhenAll(Enumerable.Range(0, 2000).Select(i => Task.Run(async () => await store.TryAddAsync("s" + i, Tokens(), default))));

        await Assert.That(results.Count(r => r)).IsEqualTo(100);
        await Assert.That(store.Count).IsEqualTo(100);
    }

    [Test]
    public async Task Invalid_input_is_rejected()
    {
        var store = Store(5);

        await Assert.That(async () => await store.TryAddAsync("", Tokens(), default)).Throws<ArgumentException>();
        await Assert.That(async () => await store.TryAddAsync("x", null!, default)).Throws<ArgumentNullException>();
        await Assert.That(await store.GetAsync("", default)).IsNull();
        await Assert.That(() => new BffTokenSet("", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Session_ids_have_256_bits_of_randomness_and_a_fixed_shape()
    {
        var ids = Enumerable.Range(0, 5000).Select(_ => SessionIds.New()).ToList();

        await Assert.That(ids.Distinct().Count()).IsEqualTo(5000);
        await Assert.That(ids.All(SessionIds.IsWellFormed)).IsTrue();
        await Assert.That(SessionIds.IsWellFormed("curto")).IsFalse();
        await Assert.That(SessionIds.IsWellFormed(new string('A', 42) + "+")).IsFalse();
        await Assert.That(SessionIds.IsWellFormed(new string('A', 42) + "=")).IsFalse();
        await Assert.That(SessionIds.IsWellFormed(null)).IsFalse();
    }

    [Test]
    public async Task Renewal_keeps_the_absolute_session_end()
    {
        var original = new BffTokenSet("A1", "R1", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddHours(8));

        var renewed = original.Renewed("A2", null, DateTimeOffset.UtcNow.AddMinutes(10));

        await Assert.That(renewed.SessionExpiresAt).IsEqualTo(original.SessionExpiresAt);   // renovar não estende a sessão
        await Assert.That(renewed.RefreshToken).IsEqualTo("R1");   // sem rotação, o refresh token anterior continua
    }
}
