using System.Net.Http;
using System.Text;

namespace Loom.Core;

/// <summary>POST JSON to one host-configured endpoint with a stable receiver deduplication key.</summary>
public sealed class HttpOutboxTransport
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    public HttpOutboxTransport(HttpClient client,Uri endpoint)
    {
        if(!endpoint.IsAbsoluteUri||endpoint.Scheme is not ("https" or "http"))throw new ArgumentException("An absolute HTTP(S) endpoint is required.",nameof(endpoint));
        this.client=client;this.endpoint=endpoint;
    }
    /// <summary>Any non-success status becomes a retryable failure. Configure credentials/timeouts on the host's client.</summary>
    public async Task SendAsync(OutboxMessage message,CancellationToken cancellationToken)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,endpoint);
        request.Headers.Add("Idempotency-Key",message.Key);
        request.Content=new StringContent(message.Payload.ToJsonString(),Encoding.UTF8,"application/json");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
