using Platform;
using System.Security.Cryptography;
using System.Text;
namespace Tests;

public sealed class FakePayments : IPayments
{
    public Payment Payment = new("123", Guid.Empty, 100, "approved", "BRL");
    public int Refunds; public bool Fail;
    public Task<string> Checkout(Guid id, decimal amount, CancellationToken ct) => Task.FromResult("https://checkout.example");
    public Task<Payment> Get(string id, CancellationToken ct) => Task.FromResult(Payment);
    public Task Refund(string id, Guid orderId, CancellationToken ct)
    { Refunds++; if (Fail) throw new HttpRequestException(); return Task.CompletedTask; }
}
public class WorkflowTests
{
    [Fact]
    public async Task BudgetRejectsFractionOfCent()
        => await Assert.ThrowsAsync<ArgumentException>(() => Apply(new State(), "GenerateBudget", new(Amount: 100.001m)));
    [Fact]
    public async Task LatePaymentUsesDurableRefundAndToleratesRepeatedNotifications()
    {
        var state = new State { Id = Guid.NewGuid(), Status = "Canceled", Data = new(Amount: 100) };
        provider.Payment = provider.Payment with { OrderId = state.Id };
        await Apply(state, "VerifyPayment", new(PaymentId: "123"));
        Assert.Equal("RefundPending", state.Status);
        Assert.Equal(0, provider.Refunds);
        Assert.Single(state.Outbox);
        await Apply(state, "VerifyPayment", new(PaymentId: "123"));
        await Apply(state, "CancelBudget");
        Assert.Single(state.Outbox);
        await Apply(state, "RefundPayment");
        provider.Payment = provider.Payment with { Status = "refunded" };
        await Apply(state, "VerifyPayment", new(PaymentId: "123"));
        await Apply(state, "RefundPayment");
        await Apply(state, "GenerateBudget", new(Amount: 100));
        Assert.Equal("Refunded", state.Status);
        Assert.Equal(1, provider.Refunds);
        provider.Payment = provider.Payment with { Id = "999" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(state, "VerifyPayment", new(PaymentId: "999")));
    }
    [Fact]
    public async Task RefundRetryRetainsDurablePendingState()
    {
        var store = new MemoryStore(); var provider = new FakePayments();
        var processor = new Processor(store, new ServiceWorkflow(provider));
        var id = Guid.NewGuid();
        await store.Save(new State { Id = id, Status = "Paid", Data = new(Amount: 100, PaymentId: "123") }, 0);
        await processor.Handle(Message.Create(id, "CancelBudget", "billing", new()));
        var pending = (await store.Get(id))!;
        Assert.Equal("RefundPending", pending.Status);
        var refund = pending.Outbox.Single();
        provider.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => processor.Handle(refund));
        Assert.Equal("RefundPending", (await store.Get(id))!.Status);
        provider.Fail = false; await processor.Handle(refund); await processor.Handle(refund);
        Assert.Equal("Refunded", (await store.Get(id))!.Status);
        Assert.Equal(2, provider.Refunds);
    }
    readonly FakePayments provider = new();
    async Task Apply(State s, string type, Payload? data = null) => await new ServiceWorkflow(provider).Apply(s, Message.Create(s.Id, type, "billing", data ?? new()), default);
    [Fact]
    public async Task FullPaymentFlow()
    {
        var s = new State { Id = Guid.NewGuid() }; provider.Payment = provider.Payment with { OrderId = s.Id };
        await Apply(s, "GenerateBudget", new(Amount: 100));
        await Apply(s, "DecideBudget", new(Approved: true)); await Apply(s, "CreateCheckout"); await Apply(s, "CreateCheckout");
        Assert.Equal("https://checkout.example", s.Data.CheckoutUrl);
        await Apply(s, "VerifyPayment", new(PaymentId: "123")); await Apply(s, "VerifyPayment", new(PaymentId: "123"));
        Assert.Equal("Paid", s.Status); Assert.Equal("123", s.Data.PaymentId);
        await Apply(s, "CancelBudget"); Assert.Equal("RefundPending", s.Status);
        await Apply(s, "RefundPayment"); Assert.Equal("Refunded", s.Status); Assert.Equal(1, provider.Refunds);
    }
    [Fact]
    public async Task FailedRefundDoesNotAcknowledgeCompensation()
    {
        var s = new State { Id = Guid.NewGuid(), Status = "Paid", Data = new(Amount: 100, PaymentId: "123") };
        await Apply(s, "CancelBudget");
        provider.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => Apply(s, "RefundPayment"));
        Assert.Equal("RefundPending", s.Status); Assert.DoesNotContain(s.Outbox, x => x.Type == "BudgetCanceled");
        provider.Fail = false; await Apply(s, "RefundPayment"); Assert.Equal("Refunded", s.Status);
    }
    [Fact]
    public async Task RejectedBudgetAndLatePayment()
    {
        var s = new State { Id = Guid.NewGuid() }; provider.Payment = provider.Payment with { OrderId = s.Id };
        await Apply(s, "GenerateBudget", new(Amount: 100)); await Apply(s, "DecideBudget", new(Approved: false));
        Assert.Equal("BudgetRejected", s.Outbox.Last().Type);
        await Apply(s, "CancelBudget");
        await Apply(s, "VerifyPayment", new(PaymentId: "123")); Assert.Equal("RefundPending", s.Status);
        await Apply(s, "RefundPayment"); Assert.Equal("Refunded", s.Status);
        await Apply(s, "CancelBudget"); Assert.Equal("Refunded", s.Status);
    }
    [Fact]
    public async Task InvalidProviderPaymentsRejected()
    {
        var s = new State { Id = Guid.NewGuid(), Status = "WaitingPayment", Data = new(Amount: 100) };
        await Assert.ThrowsAsync<ArgumentException>(() => Apply(s, "VerifyPayment", new(PaymentId: "123")));
        provider.Payment = provider.Payment with { OrderId = s.Id, Amount = 99 };
        await Assert.ThrowsAsync<ArgumentException>(() => Apply(s, "VerifyPayment", new(PaymentId: "123")));
        provider.Payment = provider.Payment with { Amount = 100, Currency = "USD" };
        await Assert.ThrowsAsync<ArgumentException>(() => Apply(s, "VerifyPayment", new(PaymentId: "123")));
        provider.Payment = provider.Payment with { Currency = "BRL", Status = "pending" };
        await Apply(s, "VerifyPayment", new(PaymentId: "123")); Assert.Equal("WaitingPayment", s.Status);
        provider.Payment = provider.Payment with { Status = "approved" }; await Apply(s, "VerifyPayment", new(PaymentId: "123"));
        provider.Payment = provider.Payment with { Id = "456" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(s, "VerifyPayment", new(PaymentId: "456")));
        await Assert.ThrowsAsync<ArgumentException>(() => Apply(new State(), "GenerateBudget", new(Amount: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => Apply(s, "Unknown"));
    }
    [Fact]
    public void WebhookHmacAndReplayWindow()
    {
        var now = DateTimeOffset.UtcNow; var ts = now.ToUnixTimeSeconds().ToString();
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"),
            Encoding.UTF8.GetBytes($"id:123;request-id:req;ts:{ts};"))).ToLowerInvariant();
        Assert.True(WebhookSignature.Verify("123", "req", $"ts={ts},v1={hash}", "secret", now));
        Assert.False(WebhookSignature.Verify("123", "req", $"ts={ts},v1={hash}", "secret", now.AddHours(1)));
        Assert.False(WebhookSignature.Verify("123", "req", $"ts={ts},v1=invalid", "secret", now));
        Assert.False(WebhookSignature.Verify("123", "req", "bad", "secret", now));
        Assert.False(WebhookSignature.Verify("", "req", "bad", "", now));
    }
}
