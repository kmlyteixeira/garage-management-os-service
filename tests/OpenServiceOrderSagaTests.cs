using Xunit;

public sealed class OpenServiceOrderSagaTests
{
    [Fact]
    public async Task Marks_order_as_failed_when_billing_rejects()
    {
        var store = new TestStore();
        var saga = new OpenServiceOrderSaga(store, new RejectingBillingClient());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            saga.StartAsync(new OpenServiceOrderRequest(Guid.NewGuid(), Guid.NewGuid(), 100), CancellationToken.None));

        Assert.Equal("BillingFailed", store.Status);
    }

    private sealed class TestStore : IServiceOrderStore
    {
        public string? Status { get; private set; }
        public Task CreateAsync(Guid id, OpenServiceOrderRequest request, Guid correlationId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetStatusAsync(Guid id, string status, CancellationToken cancellationToken)
        {
            Status = status;
            return Task.CompletedTask;
        }
        public Task<ServiceOrderResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ServiceOrderResponse?>(null);
        public Task AddOutboxAsync(ServiceOrderEvent message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<(long Id, ServiceOrderEvent Message)>> GetPendingOutboxAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(long, ServiceOrderEvent)>>(Array.Empty<(long, ServiceOrderEvent)>());
        public Task MarkOutboxPublishedAsync(long id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveCustomerAsync(CustomerResponse customer, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveVehicleAsync(VehicleResponse vehicle, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> CustomerExistsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> VehicleBelongsToCustomerAsync(Guid id, Guid customerId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task ChangeLifecycleStatusAsync(Guid id, string status, string? reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RejectingBillingClient : ISagaParticipantClient
    {
        public Task RequestQuoteAsync(Guid serviceOrderId, OpenServiceOrderRequest request, Guid correlationId, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("billing unavailable"));
    }
}
