using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Platform;
public sealed record Payment(string Id, Guid OrderId, decimal Amount, string Status, string Currency);
public interface IPayments
{
    Task<string> Checkout(Guid orderId, decimal amount, CancellationToken ct);
    Task<Payment> Get(string id, CancellationToken ct);
    Task Refund(string paymentId, Guid orderId, CancellationToken ct);
}
public sealed class MercadoPago(HttpClient client, IConfiguration config) : IPayments
{
    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var token = config["MercadoPago:AccessToken"] ?? throw new InvalidOperationException("Mercado Pago access token required.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        return request;
    }
    public async Task<string> Checkout(Guid orderId, decimal amount, CancellationToken ct)
    {
        var notification = config["MercadoPago:NotificationUrl"];
        if (!Uri.TryCreate(notification, UriKind.Absolute, out var url) || url.Scheme != "https")
            throw new InvalidOperationException("Public HTTPS notification URL required.");
        using var request = Request(HttpMethod.Post, "checkout/preferences", new {
            external_reference = orderId.ToString(),
            notification_url = notification,
            items = new[] { new { id = orderId.ToString(), title = "Ordem de servico", quantity = 1, currency_id = "BRL", unit_price = amount } }
        });
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return body.RootElement.GetProperty(config["MercadoPago:Sandbox"] == "true" ? "sandbox_init_point" : "init_point").GetString()!;
    }
    public async Task<Payment> Get(string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || id.Any(c => !char.IsAsciiDigit(c))) throw new ArgumentException("Invalid payment ID.");
        using var request = Request(HttpMethod.Get, $"v1/payments/{id}");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = body.RootElement;
        return new(root.GetProperty("id").ToString(), Guid.Parse(root.GetProperty("external_reference").GetString()!),
            root.GetProperty("transaction_amount").GetDecimal(), root.GetProperty("status").GetString()!,
            root.GetProperty("currency_id").GetString()!);
    }
    public async Task Refund(string paymentId, Guid orderId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"v1/payments/{paymentId}/refunds", new { });
        request.Headers.Add("X-Idempotency-Key", $"refund-{orderId}-{paymentId}");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (body.RootElement.GetProperty("status").GetString() != "approved")
            throw new InvalidOperationException("Refund not confirmed; compensation must be retried.");
    }
}
public static class WebhookSignature
{
    public static bool Verify(string id, string requestId, string signature, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(requestId)) return false;
        var parts = signature.Split(',').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToArray();
        var ts = parts.FirstOrDefault(x => x[0].Trim() == "ts")?[1].Trim();
        var hash = parts.FirstOrDefault(x => x[0].Trim() == "v1")?[1].Trim();
        if (!long.TryParse(ts, out var timestamp) || hash == null) return false;
        var seconds = timestamp > 9999999999 ? timestamp / 1000 : timestamp;
        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > 300) return false;
        var manifest = $"id:{id.ToLowerInvariant()};request-id:{requestId};ts:{ts};";
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(hash)); }
        catch (FormatException) { return false; }
    }
}
