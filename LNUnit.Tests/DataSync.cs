using Lnrpc;
using Routerrpc;
using ServiceStack.Text;

namespace LNUnit.Tests;

public partial class ABCLightningScenario
{
    [Test]
    [Category("Payment")]
    [Category("Invoice")]
    [Category("Sync")]
    [NonParallelizable]
    public async Task ListInvoiceAndPaymentPaging()
    {
        var invoices = await _LNUnitBuilder.GeneratePaymentsRequestFromAlias("alice", 10, new Invoice
        {
            Memo = "Things are too slow, never gonna work",
            Expiry = 100,
            ValueMsat = 1003 //1 satoshi, fees will be 0 because it is direct peer,
        });

        var alice = await _LNUnitBuilder.WaitUntilAliasIsServerReady("alice");
        var bob = await _LNUnitBuilder.WaitUntilAliasIsServerReady("bob");

        //purge data
        await bob.LightningClient.DeleteAllPaymentsAsync(new DeleteAllPaymentsRequest());

        foreach (var invoice in invoices)
        {
            var payment = await _LNUnitBuilder.MakeLightningPaymentFromAlias("bob", new SendPaymentRequest
            {
                PaymentRequest = invoice.PaymentRequest,
                FeeLimitSat = 100000000,
                TimeoutSeconds = 50
            });
            Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);
        }


        await Task.Delay(2000);

        var lp = await bob.LightningClient.ListPaymentsAsync(new ListPaymentsRequest
        {
            CreationDateStart = (ulong)DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)).ToUnixTime(),
            CreationDateEnd = (ulong)DateTime.UtcNow.AddDays(1).ToUnixTime(),
            IncludeIncomplete = false,
            Reversed = true,
            MaxPayments = 10
        });
        Assert.That(lp.Payments.Count == 10);
        await Task.Delay(200);

        var li = await alice.LightningClient.ListInvoicesAsync(new ListInvoiceRequest
        {
            CreationDateStart = (ulong)DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)).ToUnixTime(),
            CreationDateEnd = (ulong)DateTime.UtcNow.AddDays(1).ToUnixTime(),
            NumMaxInvoices = 10,
            Reversed = true,
            PendingOnly = false
        });
        Assert.That(li.Invoices.Count == 10);
        Assert.That(li.Invoices.First().State == Invoice.Types.InvoiceState.Settled);
    }

    [Test]
    [Category("Payment")]
    [Category("Invoice")]
    [Category("Sync")]
    [NonParallelizable]
    public async Task ListInvoiceAndPaymentNoDatePage()
    {
        var invoice = await _LNUnitBuilder.GeneratePaymentRequestFromAlias("alice", new Invoice
        {
            Memo = "Things are too slow, never gonna work",
            Expiry = 100,
            ValueMsat = 1003 //1 satoshi, fees will be 0 because it is direct peer,
        });

        var alice = await _LNUnitBuilder.WaitUntilAliasIsServerReady("alice");
        var bob = await _LNUnitBuilder.WaitUntilAliasIsServerReady("bob");

        //purge data
        await bob.LightningClient.DeleteAllPaymentsAsync(new DeleteAllPaymentsRequest());

        var payment = await _LNUnitBuilder.MakeLightningPaymentFromAlias("bob", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitSat = 100000000,
            TimeoutSeconds = 5
        });
        Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);

        await Task.Delay(1000);

        var lp = await bob.LightningClient.ListPaymentsAsync(new ListPaymentsRequest
        {
            IncludeIncomplete = false,
            MaxPayments = 10
        });
        Assert.That(lp.Payments.Count == 1);
        await Task.Delay(2000);

        var li = await alice.LightningClient.ListInvoicesAsync(new ListInvoiceRequest
        {
            NumMaxInvoices = 10,
            Reversed = true,
            PendingOnly = false
        });
        Assert.That(li.Invoices.Count > 0);
        Assert.That(li.Invoices.First().State == Invoice.Types.InvoiceState.Settled);
    }
}