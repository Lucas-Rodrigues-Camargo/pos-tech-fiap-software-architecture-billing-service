namespace Platform;
public sealed class ServiceWorkflow(IPayments payments) : IWorkflow
{
    public async Task Apply(State s, Message m, CancellationToken ct)
    {
        switch (m.Type)
        {
            case "GenerateBudget":
                if (s.Status is "Canceled" or "Refunded") break;
                s.Require("New");
                if (m.Data.Amount <= 0 || decimal.Round(m.Data.Amount, 2) != m.Data.Amount)
                    throw new ArgumentException("Positive budget with at most two decimal places required.");
                s.Data = m.Data; s.Move("WaitingApproval"); s.Emit(m, "BudgetGenerated", "os"); break;
            case "DecideBudget":
                s.Require("WaitingApproval");
                if (!m.Data.Approved) { s.Move("Rejected"); s.Emit(m, "BudgetRejected", "os"); break; }
                s.Move("WaitingPayment"); s.Emit(m, "BudgetApproved", "os"); break;
            case "CreateCheckout":
                s.Require("WaitingPayment");
                if (string.IsNullOrEmpty(s.Data.CheckoutUrl))
                    s.Data = s.Data with { CheckoutUrl = await payments.Checkout(s.Id, s.Data.Amount, ct) };
                break;
            case "VerifyPayment":
                s.Require("WaitingPayment", "Paid", "Canceled", "RefundPending", "Refunded");
                var payment = await payments.Get(m.Data.PaymentId, ct);
                if (payment.OrderId != s.Id || payment.Amount != s.Data.Amount || payment.Currency != "BRL")
                    throw new ArgumentException("Payment reference, currency or amount mismatch.");
                if (s.Status is "RefundPending" or "Refunded")
                {
                    if (s.Data.PaymentId != payment.Id)
                        throw new InvalidOperationException("Additional payment requires reconciliation.");
                    break;
                }
                if (payment.Status != "approved") break;
                if (s.Status == "Paid")
                {
                    if (s.Data.PaymentId != payment.Id) throw new InvalidOperationException("Duplicate payment requires operator review.");
                    break;
                }
                s.Data = s.Data with { PaymentId = payment.Id };
                if (s.Status == "Canceled")
                {
                    s.Move("RefundPending");
                    s.Emit(m, "RefundPayment", "billing");
                    break;
                }
                s.Move("Paid"); s.Emit(m, "PaymentConfirmed", "os"); break;
            case "CancelBudget":
                if (s.Status == "RefundPending") break;
                if (s.Status == "Paid")
                {
                    s.Move("RefundPending");
                    s.Emit(m, "RefundPayment", "billing");
                    break;
                }
                else if (s.Status != "Refunded") s.Move("Canceled");
                s.Emit(m, "BudgetCanceled", "os"); break;
            case "RefundPayment":
                if (s.Status == "Refunded") break;
                s.Require("RefundPending");
                await payments.Refund(s.Data.PaymentId, s.Id, ct);
                s.Move("Refunded");
                s.Emit(m, "BudgetCanceled", "os"); break;
            default: throw new ArgumentException("Unknown event type.");
        }
    }
}
