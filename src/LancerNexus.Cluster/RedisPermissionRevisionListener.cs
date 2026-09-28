using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Cluster;

/// <summary>Redis Pub/Sub adapter with SQL snapshot resync after every reconnect.</summary>
public sealed class RedisPermissionRevisionListener(string endpoint, PermissionService permissions,
    PermissionSyncCoordinator coordinator, Action<Exception>? onError = null)
{
    private const string Channel = "lancer-nexus:permissions:revision";

    public async Task RunAsync(CancellationToken ct)
    {
        if (!TryEndpoint(endpoint, out var host, out var port)) throw new ArgumentException("Redis endpoint must be host:port.", nameof(endpoint));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, ct);
                await using var stream = client.GetStream();
                await stream.WriteAsync(SubscribeCommand(), ct);
                var subscribed = await ReadRespAsync(stream, ct);
                if (subscribed is not object?[] { Length: 3 }) throw new IOException("Redis did not confirm the permission subscription.");
                if (!await coordinator.InitializeAsync(ct)) throw new IOException("Permission snapshot could not be loaded and acknowledged.");
                while (!ct.IsCancellationRequested)
                {
                    var response = await ReadRespAsync(stream, ct);
                    if (response is not object?[] { Length: 3 } message || !Equals(message[0], "message") || message[2] is not string payload)
                        continue;
                    var notice = JsonSerializer.Deserialize<PermissionRevisionChanged>(payload);
                    if (notice is null || !await coordinator.ApplyAsync(notice, ct))
                        throw new IOException("Permission revision was not applied and acknowledged.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                permissions.MarkUnsynchronized();
                onError?.Invoke(e);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static byte[] SubscribeCommand()
    {
        var args = new[] { "SUBSCRIBE", Channel };
        var output = new StringBuilder($"*{args.Length}\r\n");
        foreach (var arg in args) output.Append('$').Append(Encoding.UTF8.GetByteCount(arg)).Append("\r\n").Append(arg).Append("\r\n");
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static async Task<object?> ReadRespAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[1];
        if (await stream.ReadAsync(prefix, ct) != 1) throw new IOException("Redis closed the permission subscription.");
        var line = await ReadLineAsync(stream, ct);
        return prefix[0] switch
        {
            (byte)'+' or (byte)'-' => line,
            (byte)':' => long.TryParse(line, out var number) ? number : throw new IOException("Invalid Redis integer response."),
            (byte)'$' => await ReadBulkAsync(stream, line, ct),
            (byte)'*' => await ReadArrayAsync(stream, line, ct),
            _ => throw new IOException("Unsupported Redis response type.")
        };
    }

    private static async Task<object?[]> ReadArrayAsync(Stream stream, string lengthText, CancellationToken ct)
    {
        if (!int.TryParse(lengthText, out var count) || count is < 0 or > 8) throw new IOException("Invalid Redis array length.");
        var values = new object?[count];
        for (var i = 0; i < count; i++) values[i] = await ReadRespAsync(stream, ct);
        return values;
    }

    private static async Task<string?> ReadBulkAsync(Stream stream, string lengthText, CancellationToken ct)
    {
        if (!int.TryParse(lengthText, out var length) || length < -1 || length > 1024 * 1024) throw new IOException("Invalid Redis bulk length.");
        if (length == -1) return null;
        var bytes = new byte[length + 2];
        await ReadExactlyAsync(stream, bytes, ct);
        if (bytes[^2] != '\r' || bytes[^1] != '\n') throw new IOException("Invalid Redis bulk terminator.");
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        while (buffer.Length <= 1024)
        {
            if (await stream.ReadAsync(single, ct) != 1) throw new IOException("Redis closed a response line.");
            if (single[0] == '\n')
            {
                var bytes = buffer.ToArray();
                if (bytes.Length == 0 || bytes[^1] != '\r') throw new IOException("Invalid Redis line ending.");
                return Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
            }
            buffer.WriteByte(single[0]);
        }
        throw new IOException("Redis response line exceeded its size limit.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> bytes, CancellationToken ct)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes[offset..], ct);
            if (count == 0) throw new IOException("Redis closed a bulk response.");
            offset += count;
        }
    }

    private static bool TryEndpoint(string value, out string host, out int port)
    {
        host = ""; port = 0;
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out port) || port is < 1 or > 65535) return false;
        host = value[..separator].Trim();
        return host.Length > 0;
    }
}
