using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace TEC.Bff.Theme;

/// <summary>
/// Defesa contra SSRF na busca do manifesto (FRT-TEM-05): a URL é fixa na configuração, mas o DNS dela pode apontar para dentro da
/// rede. A conexão só segue para endereço público, resolvido e fixado no momento de conectar (sem janela para troca de DNS).
/// </summary>
internal static class ThemeNetwork
{
    /// <summary>Endereço roteável na internet pública. Recusa loopback, rede privada, link-local (inclui o serviço de metadados de nuvem), CGNAT, multicast, reservados e as formas IPv6 que embutem IPv4.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        byte[] b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? IsPublicV4(b) : address.AddressFamily == AddressFamily.InterNetworkV6 && IsPublicV6(b);
    }

    private static bool IsPublicV4(byte[] b) => !(
        b[0] == 0                                          // 0.0.0.0/8
        || b[0] == 10                                      // 10/8
        || b[0] == 100 && (b[1] & 0xC0) == 64              // 100.64/10 (CGNAT)
        || b[0] == 127                                     // loopback
        || b[0] == 169 && b[1] == 254                      // link-local (metadados de nuvem)
        || b[0] == 172 && (b[1] & 0xF0) == 16              // 172.16/12
        || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2      // 192.0.0/24 e 192.0.2/24
        || b[0] == 192 && b[1] == 88 && b[2] == 99         // 6to4 relay
        || b[0] == 192 && b[1] == 168                      // 192.168/16
        || b[0] == 198 && (b[1] & 0xFE) == 18              // 198.18/15 (benchmark)
        || b[0] == 198 && b[1] == 51 && b[2] == 100        // documentação
        || b[0] == 203 && b[1] == 0 && b[2] == 113         // documentação
        || b[0] >= 224);                                   // multicast, reservado e broadcast

    private static bool IsPublicV6(byte[] b)
    {
        // ::/96 (inclui :: e ::1 e o IPv4-compatível)
        if (b.Take(12).All(x => x == 0))
            return false;
        return !(
            (b[0] & 0xFE) == 0xFC                                  // fc00::/7 (ULA)
            || b[0] == 0xFE && (b[1] & 0xC0) is 0x80 or 0xC0       // fe80::/10 e fec0::/10
            || b[0] == 0xFF                                        // multicast
            || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8   // 2001:db8::/32
            || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0         // Teredo
            || b[0] == 0x20 && b[1] == 0x02                        // 6to4
            || b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B);    // 64:ff9b::/96 (NAT64)
    }

    /// <summary>Handler da busca do tema: sem redirecionamento (um 3xx é falha), sem cookie, sem proxy e, por padrão, só para endereço público.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        if (!allowPrivateNetworks)
            handler.ConnectCallback = ConnectPublicAsync;
        return handler;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("Destino recusado: o endereço não é público.");

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
        }

        throw new HttpRequestException("Falha ao conectar ao destino.", last);
    }
}
