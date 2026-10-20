using System.Text.Json;
using System.Text.Json.Nodes;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class MessageContractsTests
{
    // Representative JSON wire format, not the yet-to-be-selected broker's
    // serializer configuration. Consumers must explicitly configure and validate
    // their own deserializers when transport integration is implemented.
    private static readonly JsonSerializerOptions WireJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true
        };

    [Fact]
    public void Checkout_requested_contract_round_trips_through_json()
    {
        var requested = new CheckoutRequestedV1(
            Guid.CreateVersion7(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero));

        string json = JsonSerializer.Serialize(requested, WireJsonOptions);
        var restored = JsonSerializer.Deserialize<CheckoutRequestedV1>(json, WireJsonOptions);

        Assert.Equal(requested, restored);
        Assert.Contains("\"orderId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"messageId\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("idempotencyKey", json, StringComparison.OrdinalIgnoreCase);

        // Additive JSON properties should not break older consumers.
        var futureMessage = JsonNode.Parse(json)!.AsObject();
        futureMessage["someFutureField"] = "future value";
        Assert.Equal(requested,
            JsonSerializer.Deserialize<CheckoutRequestedV1>(
                futureMessage.ToJsonString(), WireJsonOptions));

        // Missing nonoptional constructor parameters are rejected ONLY because
        // RespectRequiredConstructorParameters is enabled on the consumer.
        var incompleteMessage = JsonNode.Parse(json)!.AsObject();
        Assert.True(incompleteMessage.Remove("buyerId"));
        Assert.Throws<JsonException>(() =>
        {
            _ = JsonSerializer.Deserialize<CheckoutRequestedV1>(
                incompleteMessage.ToJsonString(), WireJsonOptions);
        });
    }

    [Fact]
    public void Inventory_command_and_outcomes_round_trip_through_json()
    {
        var orderId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);
        var request = new InventorySaleRequestedV1(
            Guid.CreateVersion7(), orderId, holdId, Guid.NewGuid(), at);
        var committed = new InventorySaleCommittedV1(
            Guid.CreateVersion7(), orderId, holdId, at);
        var rejected = new InventorySaleRejectedV1(
            Guid.CreateVersion7(), orderId, holdId, "HoldExpired", at);

        Assert.Equal(request, JsonSerializer.Deserialize<InventorySaleRequestedV1>(
            JsonSerializer.Serialize(request, WireJsonOptions), WireJsonOptions));
        Assert.Equal(committed, JsonSerializer.Deserialize<InventorySaleCommittedV1>(
            JsonSerializer.Serialize(committed, WireJsonOptions), WireJsonOptions));
        Assert.Equal(rejected, JsonSerializer.Deserialize<InventorySaleRejectedV1>(
            JsonSerializer.Serialize(rejected, WireJsonOptions), WireJsonOptions));

        Assert.Equal(orderId, committed.OrderId);
        Assert.Equal(orderId, rejected.OrderId);
        Assert.Equal(holdId, request.HoldId);
    }

    [Fact]
    public void Contracts_assembly_has_no_business_or_ef_dependencies()
    {
        var referencedNames = typeof(CheckoutRequestedV1).Assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(referencedNames, x =>
            x.StartsWith("TicketRush.", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedNames, x =>
            x.Contains("EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedNames, x =>
            x.StartsWith("MassTransit", StringComparison.Ordinal));
    }
}
