using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Platform;
namespace Tests;
public class MercadoPagoTests
{
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            =>Task.FromResult(respond(request));
    }
    static HttpResponseMessage Json(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    [Fact] public async Task CheckoutLookupAndRefundUseOfficialContract()
    {
        var orderId=Guid.NewGuid(); var calls=0;
        var client=new HttpClient(new Handler(request=>{
            Assert.Equal("Bearer token",request.Headers.Authorization!.ToString());calls++;
            if(request.RequestUri!.AbsolutePath=="/checkout/preferences")return Json("""{"init_point":"https://checkout","sandbox_init_point":"https://sandbox"}""");
            if(request.Method==HttpMethod.Get)return Json($$"""{"id":123,"external_reference":"{{orderId}}","transaction_amount":100,"status":"approved","currency_id":"BRL"}""");
            Assert.Equal($"refund-{orderId}-123",request.Headers.GetValues("X-Idempotency-Key").Single());
            return Json("""{"status":"approved"}""");
        })){BaseAddress=new Uri("https://api.mercadopago.com/")};
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["MercadoPago:AccessToken"]="token",["MercadoPago:NotificationUrl"]="https://callback",["MercadoPago:Sandbox"]="true"}).Build();
        var gateway=new MercadoPago(client,config);
        Assert.Equal("https://sandbox",await gateway.Checkout(orderId,100,default));
        var payment=await gateway.Get("123",default);
        Assert.Equal(orderId,payment.OrderId);Assert.Equal(100,payment.Amount);
        await gateway.Refund("123",orderId,default);Assert.Equal(3,calls);
        await Assert.ThrowsAsync<ArgumentException>(()=>gateway.Get("../bad",default));
    }
    [Fact] public async Task FailedRefundAndMissingConfigurationAreNotSuccess()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["MercadoPago:AccessToken"]="token",["MercadoPago:NotificationUrl"]="http://invalid"}).Build();
        var gateway=new MercadoPago(new HttpClient(new Handler(_=>Json("""{"status":"pending"}"""))){BaseAddress=new Uri("https://api.mercadopago.com/")},config);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>gateway.Checkout(Guid.NewGuid(),100,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>gateway.Refund("123",Guid.NewGuid(),default));
        var empty=new MercadoPago(new HttpClient(),new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>empty.Get("123",default));
    }
}
