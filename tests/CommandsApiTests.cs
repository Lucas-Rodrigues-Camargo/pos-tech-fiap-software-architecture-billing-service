using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Platform;
namespace Tests;
public class CommandsApiTests
{
    [Fact] public async Task ApprovalCheckoutAndUnsignedWebhook()
    {
        using var app=new ApiFactory();using var client=app.CreateClient();
        client.DefaultRequestHeaders.Authorization=new("Bearer",ApiFactory.Token("Customer"));
        var id=Guid.NewGuid();await app.Store.Save(new State{Id=id,Status="WaitingApproval",Data=new(Cpf:"52998224725",Amount:100)},0);
        client.DefaultRequestHeaders.Add("Idempotency-Key",Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/budgets/{id}/decision",new{approved=true})).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");client.DefaultRequestHeaders.Add("Idempotency-Key",Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsync($"/api/budgets/{id}/checkout",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/webhooks/mercadopago?data.id=123",new{})).StatusCode);
    }
}
