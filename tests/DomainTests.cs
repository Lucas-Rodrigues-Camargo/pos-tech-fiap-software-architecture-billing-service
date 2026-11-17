using Billing;
namespace Tests;
public class DomainTests
{
    [Fact] public void ApprovedBudgetReceivesVerifiedPayment()
    {
        var budget = new Budget(Guid.NewGuid(), 100);
        budget.Decide(true); budget.ConfirmPayment("payment-1", 100); budget.ConfirmPayment("payment-1", 100);
        Assert.Equal(BudgetStatus.Paid, budget.Status);
        Assert.Equal("payment-1", budget.ProviderPaymentId);
    }
    [Fact] public void PaymentBeforeApprovalIsRejected()
        => Assert.Throws<InvalidOperationException>(() => new Budget(Guid.NewGuid(), 100).ConfirmPayment("p", 100));
    [Fact] public void PaymentAmountMustMatch()
    {
        var budget = new Budget(Guid.NewGuid(), 100); budget.Decide(true);
        Assert.Throws<ArgumentException>(() => budget.ConfirmPayment("p", 99));
        Assert.Throws<ArgumentException>(() => budget.ConfirmPayment("", 100));
    }
    [Fact] public void PaidBudgetMustRefundBeforeCompletingCompensation()
    {
        var budget = new Budget(Guid.NewGuid(), 100); budget.Decide(true); budget.ConfirmPayment("p", 100);
        budget.Compensate(); budget.Compensate(); Assert.Equal(BudgetStatus.RefundPending, budget.Status);
        budget.ConfirmRefund(); budget.Compensate(); Assert.Equal(BudgetStatus.Refunded, budget.Status);
    }
    [Fact] public void RejectedBudgetCannotReceivePayment()
    {
        var budget = new Budget(Guid.NewGuid(), 100); budget.Decide(false);
        Assert.Equal(BudgetStatus.Rejected, budget.Status);
        Assert.Throws<InvalidOperationException>(() => budget.Decide(true));
        budget.Compensate(); budget.Compensate(); Assert.Equal(BudgetStatus.Canceled, budget.Status);
        Assert.Throws<InvalidOperationException>(() => budget.ConfirmRefund());
    }
    [Fact] public void InvalidBudgetIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Budget(Guid.Empty, 100));
        Assert.Throws<ArgumentException>(() => new Budget(Guid.NewGuid(), 0));
    }
}

