namespace Billing;
public enum BudgetStatus { WaitingApproval, Rejected, WaitingPayment, Paid, RefundPending, Refunded, Canceled }
public sealed class Budget
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid OrderId { get; }
    public decimal Amount { get; }
    public BudgetStatus Status { get; private set; } = BudgetStatus.WaitingApproval;
    public string? ProviderPaymentId { get; private set; }
    public Budget(Guid orderId, decimal amount)
    {
        if (orderId == Guid.Empty || amount <= 0) throw new ArgumentException("Order and positive amount required.");
        OrderId = orderId; Amount = amount;
    }
    public void Decide(bool approved)
    {
        Require(BudgetStatus.WaitingApproval);
        Status = approved ? BudgetStatus.WaitingPayment : BudgetStatus.Rejected;
    }
    public void ConfirmPayment(string providerPaymentId, decimal paidAmount)
    {
        if (Status == BudgetStatus.Paid && ProviderPaymentId == providerPaymentId) return;
        Require(BudgetStatus.WaitingPayment);
        if (string.IsNullOrWhiteSpace(providerPaymentId) || paidAmount != Amount)
            throw new ArgumentException("Verified payment must match budget.");
        ProviderPaymentId = providerPaymentId; Status = BudgetStatus.Paid;
    }
    public void Compensate()
    {
        if (Status is BudgetStatus.Canceled or BudgetStatus.Refunded or BudgetStatus.RefundPending) return;
        Status = Status == BudgetStatus.Paid ? BudgetStatus.RefundPending : BudgetStatus.Canceled;
    }
    public void ConfirmRefund()
    {
        Require(BudgetStatus.RefundPending); Status = BudgetStatus.Refunded;
    }
    private void Require(BudgetStatus status)
    {
        if (Status != status) throw new InvalidOperationException($"Expected {status}, got {Status}.");
    }
}

