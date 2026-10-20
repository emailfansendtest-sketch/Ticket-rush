
namespace TicketRush.IntegrationTests.Utilities
{
    internal class TimeProviderMock : TimeProvider
    {
        public DateTimeOffset CurrentTimeUtc { get; set; }

        public override DateTimeOffset GetUtcNow() => CurrentTimeUtc;
    }
}
