using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using TicketRush.Api;
using TicketRush.Domain;
using Xunit;

namespace TicketRush.IntegrationTests;

/// <summary>Checks cross-process contract semantics without a mock database.</summary>
public sealed class InventoryHttpServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.Created, ResultKind.Created)]
    [InlineData(HttpStatusCode.OK, ResultKind.Replayed)]
    public async Task Adapter_preserves_hold_status_and_forwards_request_token(
        HttpStatusCode status, ResultKind expected)
    {
        Guid buyer = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        var hold = new HoldView(Guid.NewGuid(), eventId, buyer, ["A-001"],
            DateTimeOffset.UtcNow.AddMinutes(10), "Active");
        var requests = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("sample-test-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/events/{eventId:D}/holds", request.RequestUri?.AbsolutePath);
            Interlocked.Increment(ref requests);
            return new HttpResponseMessage(status) { Content = JsonContent.Create(hold) };
        })) { BaseAddress = new Uri("http://inventory-api/") };
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer sample-test-token";
        var service = new HttpInventoryService(http, new HttpContextAccessor { HttpContext = context });
        var result = await service.HoldAsync(eventId, buyer, "idempotency-1", ["A-001"], CancellationToken.None);
        Assert.Equal(expected, result.Kind);
        Assert.NotNull(result.Value);
        Assert.Equal(hold.Id, result.Value.Id);
        Assert.Equal(hold.SeatCodes, result.Value.SeatCodes);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Adapter_reports_conflict_without_creating_a_hold()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)))
        { BaseAddress = new Uri("http://inventory-api/") };
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer sample-test-token";
        var service = new HttpInventoryService(http, new HttpContextAccessor { HttpContext = context });
        var outcome = await service.HoldAsync(Guid.NewGuid(), Guid.NewGuid(), "key", ["A-002"], CancellationToken.None);
        Assert.Equal(ResultKind.Conflict, outcome.Kind);
        Assert.Null(outcome.Value);
    }

    [Fact]
    public async Task Adapter_rejects_missing_authenticated_bearer_before_sending()
    {
        var sent = false;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            sent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("http://inventory-api/") };
        var service = new HttpInventoryService(http, new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetHoldAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
        Assert.False(sent);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
