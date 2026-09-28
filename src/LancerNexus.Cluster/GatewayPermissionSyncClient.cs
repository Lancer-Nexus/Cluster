using System.Net.Http.Headers;
using System.Net.Http.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Cluster;

/// <summary>Authenticated snapshot and revision ACK adapter for a game instance.</summary>
public sealed class GatewayPermissionSyncClient(HttpClient http, Uri gatewayBaseUri, string instanceKey)
    : IPermissionSnapshotSource, IPermissionRevisionAcknowledger
{
    public async Task<PermissionSnapshotDocument> LoadAsync(CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Get, "/api/v1/game/admin/permissions/snapshot");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PermissionSnapshotDocument>(cancellationToken)
               ?? throw new InvalidDataException("Gateway returned an empty permission snapshot.");
    }

    public async Task<bool> AcknowledgeAsync(PermissionRevisionAcknowledged acknowledgement,
        CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Post, "/api/v1/game/admin/permissions/ack");
        request.Content = JsonContent.Create(acknowledgement);
        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private HttpRequestMessage Create(HttpMethod method, string path)
    {
        if (gatewayBaseUri.Scheme != Uri.UriSchemeHttps || instanceKey.Length < 32)
            throw new InvalidOperationException("Permission sync requires HTTPS and a private instance credential.");
        var request = new HttpRequestMessage(method, new Uri(gatewayBaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceKey);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }
}
