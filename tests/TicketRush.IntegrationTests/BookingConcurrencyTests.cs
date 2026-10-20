using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.IntegrationTests.Utilities;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class BookingConcurrencyTests
{
    private static readonly Guid DemoEvent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task One_thousand_buyers_competing_for_one_hundred_seats_never_oversell()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;

        await TestDatabaseInitializer.InitializeAsync( options );

        // Every seat is targeted by 10 independent buyers. Limit *in-flight* work to
        // avoid 1,000 open sockets; all 1,000 attempts are started as tasks.
        using var limiter = new SemaphoreSlim(64);

        var attempts = Enumerable.Range(0, 1_000).Select(async i =>
        {
            await limiter.WaitAsync();
            try
            {
                await using var db = new BookingDbContext(options);

                var service = new InventoryService(db, TimeProvider.System);

                Guid buyer = Guid.NewGuid();

                return await service.HoldAsync(DemoEvent, buyer,
                    $"hold-attempt-{i}", [$"A-{(i % 100) + 1:000}"], CancellationToken.None);
            }
            finally { limiter.Release(); }
        });
        var results = await Task.WhenAll(attempts);

        Assert.Equal(100, results.Count(x => x.Kind == ResultKind.Created));

        Assert.Equal(900, results.Count(x => x.Kind == ResultKind.Conflict));

        var validHolds = results.Where(x => x.Kind == ResultKind.Created)
            .Select(x => x.Value!).ToArray();

        var orders = await Task.WhenAll(validHolds.Select(async (hold, index) =>
        {
            await using var db = new BookingDbContext(options);

            var service = new BookingService(db, TimeProvider.System);

            return await service.ConfirmAsync(hold.Id, hold.BuyerId,
                $"checkout-{index}", CancellationToken.None);
        }));

        Assert.All(orders, x => Assert.Equal(ResultKind.Created, x.Kind));

        await using var verify = new BookingDbContext(options);

        Assert.Equal(100, await verify.Seats.CountAsync(x => x.Status == SeatStatus.Sold));

        Assert.Equal(100, await verify.Orders.CountAsync());

        Assert.Equal(100, await verify.Seats.Where(x => x.Status == SeatStatus.Sold)
            .Select(x => x.SoldOrderId).Distinct().CountAsync());
    }


    [Fact]
    public async Task Two_buyers_competing_for_overlapping_sets_of_seats_never_oversell()
    {
        await using var postgres =
            new PostgreSqlBuilder( "postgres:17-alpine" ).Build();

        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql( postgres.GetConnectionString() )
            .Options;

        await TestDatabaseInitializer.InitializeAsync( options );

        static string SeatCode( int index ) => $"A-{index + 1:000}";

        foreach(int seatsPerBuyer in new[] { 1, 2, 4, 6 })
        {
            for(int overlapCount = 0;
                 overlapCount <= seatsPerBuyer;
                 overlapCount++)
            {
                // With no overlap, the starting position is irrelevant.
                int startCount = overlapCount == 0
                    ? 1
                    : seatsPerBuyer - overlapCount + 1;

                for(int overlapStart = 0;
                     overlapStart < startCount;
                     overlapStart++)
                {
                    // Give every scenario its own event so that
                    // reservations from previous iterations cannot
                    // influence the current test case.
                    Guid eventId = Guid.NewGuid();

                    await using(var setup = new BookingDbContext( options ))
                    {
                        setup.Events.Add( new TicketEvent
                        {
                            Id = eventId,
                            Name = "Overlap test event",
                            StartsAtUtc = DateTimeOffset.UtcNow.AddDays( 1 )
                        } );

                        setup.Seats.AddRange(
                            Enumerable.Range( 0, seatsPerBuyer * 2 )
                                .Select( index => new SeatAllocation
                                {
                                    EventId = eventId,
                                    SeatCode = SeatCode( index ),
                                    Status = SeatStatus.Available
                                } ) );

                        await setup.SaveChangesAsync();
                    }

                    var firstBuyerRequestedCodes =
                        Enumerable.Range( 0, seatsPerBuyer )
                            .Select( SeatCode )
                            .ToArray();

                    var secondBuyerRequestedCodes =
                        firstBuyerRequestedCodes
                            .Skip( overlapStart )
                            .Take( overlapCount )
                            .Concat(
                                Enumerable.Range(
                                        seatsPerBuyer,
                                        seatsPerBuyer - overlapCount )
                                    .Select( SeatCode ) )
                            .ToArray();

                    // Both requests are released together.
                    var start = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously );

                    async Task<OperationResult<HoldView>> TryHold(
                        string key,
                        string[] codes )
                    {
                        await start.Task;

                        Guid buyerId = Guid.NewGuid();

                        await using var db = new BookingDbContext( options );
                        var booking = new InventoryService(
                            db, TimeProvider.System );

                        return await booking.HoldAsync(
                            eventId, buyerId, key, codes,
                            CancellationToken.None );
                    }

                    var firstTask = TryHold( "buyer-1", firstBuyerRequestedCodes );

                    var secondTask = TryHold( "buyer-2", secondBuyerRequestedCodes );

                    start.SetResult( true );

                    var results = await Task.WhenAll( firstTask, secondTask );

                    //If there is no overlap - both buyers succeed; if there is - one will succeed, other will fail.
                    int expectedSuccessCount =
                        overlapCount == 0 ? 2 : 1;

                    Assert.Equal(
                        expectedSuccessCount,
                        results.Count( x => x.Kind == ResultKind.Created ) );

                    Assert.Equal(
                        2 - expectedSuccessCount,
                        results.Count( x => x.Kind == ResultKind.Conflict ) );

                    var successfulHolds = results
                        .Where( x => x.Kind == ResultKind.Created )
                        .Select( x => x.Value! )
                        .ToArray();

                    // Complete every successful reservation.
                    var orderResults = await Task.WhenAll(
                        successfulHolds.Select( async hold =>
                        {
                            await using var db =
                                new BookingDbContext( options );

                            var booking = new BookingService(
                                db, TimeProvider.System );

                            return await booking.ConfirmAsync(
                                hold.Id,
                                hold.BuyerId,
                                $"checkout-{hold.Id}",
                                CancellationToken.None );
                        } ) );

                    Assert.All( orderResults,
                        result => Assert.Equal(
                            ResultKind.Created, result.Kind ) );

                    // Map every successfully purchased seat to
                    // the order that owns it.
                    var soldByCode = successfulHolds
                        .Zip( orderResults, ( hold, order ) =>
                            new { Hold = hold, Order = order.Value! } )
                        .SelectMany( x =>
                            x.Hold.SeatCodes.Select( code => new
                            {
                                Code = code,
                                OrderId = x.Order.Id
                            } ) )
                        .ToDictionary(
                            x => x.Code,
                            x => x.OrderId,
                            StringComparer.Ordinal );

                    await using var verify =
                        new BookingDbContext( options );

                    var seats = await verify.Seats
                        .AsNoTracking()
                        .Where( x => x.EventId == eventId )
                        .ToListAsync();

                    Assert.Equal( seatsPerBuyer * 2, seats.Count );

                    // Each successful buyer bought exactly
                    // seatsPerBuyer seats.
                    Assert.Equal(
                        expectedSuccessCount * seatsPerBuyer,
                        soldByCode.Count );

                    foreach(var seat in seats)
                    {
                        if(soldByCode.TryGetValue(
                            seat.SeatCode, out Guid orderId ))
                        {
                            Assert.Equal( SeatStatus.Sold, seat.Status );
                            Assert.Equal( orderId, seat.SoldOrderId );
                        }
                        else
                        {
                            // Losing requests must not leave behind
                            // partially reserved or sold seats.
                            Assert.Equal( SeatStatus.Available, seat.Status );
                            Assert.Null( seat.HoldId );
                            Assert.Null( seat.SoldOrderId );
                        }
                    }

                    // No failed request should have persisted a hold.
                    Assert.Equal(
                        expectedSuccessCount,
                        await verify.Holds.CountAsync(
                            x => x.EventId == eventId ) );

                    var holdIds = await verify.Holds
                        .Where( x => x.EventId == eventId )
                        .Select( x => x.Id )
                        .ToArrayAsync();

                    Assert.Equal(
                        expectedSuccessCount,
                        await verify.Orders.CountAsync(
                            x => holdIds.Contains( x.HoldId ) ) );
                }
            }
        }
    }

    [Fact]
    public async Task Expired_hold_reassigned_to_another_buyer_cannot_be_confirmed_by_original_buyer()
    {
        await using var postgres =
            new PostgreSqlBuilder( "postgres:17-alpine" ).Build();

        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql( postgres.GetConnectionString() )
            .Options;

        await TestDatabaseInitializer.InitializeAsync( options );
        
        var timeProviderMock = new TimeProviderMock
        {
            CurrentTimeUtc = DateTime.UtcNow.AddMinutes( -15.0 )
        };

        Guid firstBuyer = Guid.NewGuid();

        OperationResult<HoldView> firstBuyerHold;

        using var db1 = new BookingDbContext( options );

        firstBuyerHold = await new InventoryService( db1, timeProviderMock ).HoldAsync(
            DemoEvent, 
            firstBuyer, 
            "first-buyer-hold", 
            [ "A-001" ], 
            CancellationToken.None );

        Assert.NotNull( firstBuyerHold.Value );
        Assert.Equal( ResultKind.Created, firstBuyerHold.Kind );

        timeProviderMock.CurrentTimeUtc = timeProviderMock.CurrentTimeUtc.AddMinutes( 10.0 ).AddSeconds( 1.0 );

        Task<OperationResult<OrderView>> firstBuyerConfirmationTask;

        using var db2 = new BookingDbContext( options );

        firstBuyerConfirmationTask = new BookingService( db2, timeProviderMock ).ConfirmAsync( 
            firstBuyerHold.Value!.Id, 
            firstBuyerHold.Value.BuyerId,
            $"first-buyer-checkout", 
            CancellationToken.None );

        Guid secondBuyer = Guid.NewGuid();

        Task<OperationResult<HoldView>> secondBuyerHoldTask;

        using var db3 = new BookingDbContext( options );

        secondBuyerHoldTask = new InventoryService( db3, timeProviderMock ).HoldAsync( 
            DemoEvent, 
            secondBuyer, 
            "second-buyer-hold", 
            [ "A-001" ], 
            CancellationToken.None );

        await Task.WhenAll( firstBuyerConfirmationTask, secondBuyerHoldTask );

        var firstBuyerConfirmation = await firstBuyerConfirmationTask;
        Assert.Null( firstBuyerConfirmation.Value );
        Assert.Equal( ResultKind.Expired, firstBuyerConfirmation.Kind );

        var secondBuyerHold = await secondBuyerHoldTask;
        Assert.NotNull( secondBuyerHold.Value );
        Assert.Equal( ResultKind.Created, secondBuyerHold.Kind );

        await using(var verify = new BookingDbContext( options ))
        {
            var seat = await verify.Seats
                .AsNoTracking()
                .SingleAsync( x =>
                    x.EventId == DemoEvent &&
                    x.SeatCode == "A-001" );

            Assert.Equal( SeatStatus.Held, seat.Status );
            Assert.Equal( secondBuyerHold.Value.Id, seat.HoldId );
            Assert.Null( seat.SoldOrderId );

            Assert.Equal( 0, await verify.Orders.CountAsync() );
        }

        OperationResult<OrderView> secondBuyerOrder;

        using var db4 = new BookingDbContext( options );

        secondBuyerOrder = await new BookingService( db4, timeProviderMock ).ConfirmAsync(
            secondBuyerHold.Value.Id,
            secondBuyer,
            "second-buyer-checkout",
            CancellationToken.None );

        Assert.Equal( ResultKind.Created, secondBuyerOrder.Kind );
        Assert.NotNull( secondBuyerOrder.Value );

        await using(var verify = new BookingDbContext( options ))
        {
            var seat = await verify.Seats
                .AsNoTracking()
                .SingleAsync( x =>
                    x.EventId == DemoEvent &&
                    x.SeatCode == "A-001" );

            Assert.Equal( SeatStatus.Sold, seat.Status );
            Assert.Equal( secondBuyerOrder.Value.Id, seat.SoldOrderId );

            Assert.Equal( 1, await verify.Orders.CountAsync() );
        }
    }

    [Theory]
    [InlineData( "buyer_identical_key", "buyer_identical_key", ResultKind.Replayed )]
    [InlineData( "buyer_nonidentical_first_key", "buyer_nonidentical_second_key", ResultKind.Conflict )]
    public async Task Two_concurrent_checkout_requests_for_the_same_hold_cannot_create_duplicate_orders(
        string firstIdempotencyKey,
        string secondIdempotencyKey,
        ResultKind expectedResult
        )
    {
        await using var postgres = new PostgreSqlBuilder( "postgres:17-alpine" ).Build();

        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql( postgres.GetConnectionString() ).Options;

        await TestDatabaseInitializer.InitializeAsync( options );

        Guid buyer = Guid.NewGuid();

        await using var db1 = new BookingDbContext( options );

        var hold = await new InventoryService( db1, TimeProvider.System )
            .HoldAsync( DemoEvent, buyer, "same-command", [ "A-001" ], CancellationToken.None );

        Assert.Equal( ResultKind.Created, hold.Kind );
        Assert.NotNull( hold.Value );

        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously );

        async Task<OperationResult<OrderView>> ConfirmAsync(
            BookingDbContext db,
            string idempotencyKey )
        {
            await start.Task;

            var service = new BookingService(
                db, TimeProvider.System );

            return await service.ConfirmAsync(
                hold.Value.Id,
                buyer,
                idempotencyKey,
                CancellationToken.None );
        }

        await using var db2 = new BookingDbContext( options );
        await using var db3 = new BookingDbContext( options );

        var confirmFirstTask = ConfirmAsync(
            db2, firstIdempotencyKey );

        var confirmSecondTask = ConfirmAsync(
            db3, secondIdempotencyKey );

        // Release both asynchronous operations.
        start.SetResult( true );

        // Wait for both actual ConfirmAsync operations to finish.
        var results =
            await Task.WhenAll(
                confirmFirstTask,
                confirmSecondTask );

        Assert.Equal( 1, results.Count( r => r.Kind == ResultKind.Created ) );
        Assert.Equal( 1, results.Count( r => r.Kind == expectedResult ) );

        await using var verify = new BookingDbContext( options );

        var order = await verify.Orders
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal( hold.Value.Id, order.HoldId );
        Assert.Equal( buyer, order.BuyerId );

        var savedHold = await verify.Holds
            .AsNoTracking()
            .SingleAsync( x => x.Id == hold.Value.Id );

        Assert.Equal( HoldStatus.Confirmed, savedHold.Status );

        var seat = await verify.Seats
            .AsNoTracking()
            .SingleAsync( x =>
                x.EventId == DemoEvent &&
                x.SeatCode == "A-001" );

        Assert.Equal( SeatStatus.Sold, seat.Status );
        Assert.Equal( order.Id, seat.SoldOrderId );

        // For identical idempotency keys, both responses
        // must refer to the same persisted order.
        if(firstIdempotencyKey == secondIdempotencyKey)
        {
            Assert.All( results, result =>
            {
                Assert.NotNull( result.Value );
                Assert.Equal( order.Id, result.Value.Id );
            } );
        }
    }

    [Fact]
    public async Task Two_concurrent_hold_requests_with_the_same_idempotency_key_return_the_same_reservation()
    {
        await using var postgres = new PostgreSqlBuilder( "postgres:17-alpine" ).Build();

        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql( postgres.GetConnectionString() ).Options;

        await TestDatabaseInitializer.InitializeAsync( options );

        Guid buyer = Guid.NewGuid();

        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously );

        var requestedCodes = new[] { "A-001" };

        async Task<OperationResult<HoldView>> HoldAsync(
            BookingDbContext db )
        {
            await start.Task;

            var service = new InventoryService(
                db, TimeProvider.System );

            return await service.HoldAsync( 
                DemoEvent, 
                buyer, 
                "same-command",
                requestedCodes, 
                CancellationToken.None );
        }

        await using var db1 = new BookingDbContext( options );
        await using var db2 = new BookingDbContext( options );

        var holdFirstTask = HoldAsync( db1 );

        var holdSecondTask = HoldAsync( db2 );

        // Release both asynchronous operations.
        start.SetResult( true );

        var results = await Task.WhenAll( holdFirstTask, holdSecondTask );

        Assert.All( results, result =>
            Assert.NotNull( result.Value ) );

        var firstHold = results[ 0 ].Value!;
        var secondHold = results[ 1 ].Value!;

        Assert.Equal( firstHold.Id, secondHold.Id );
        Assert.Equal( firstHold.EventId, secondHold.EventId );
        Assert.Equal( firstHold.BuyerId, secondHold.BuyerId );
        Assert.Equal( firstHold.ExpiresAtUtc, secondHold.ExpiresAtUtc );
        Assert.Equal( firstHold.SeatCodes, secondHold.SeatCodes );
        Assert.Equal(
            1,
            results.Count( r => r.Kind == ResultKind.Created ) );

        Assert.Equal(
            1,
            results.Count( r => r.Kind == ResultKind.Replayed ) );

        await using var verify = new BookingDbContext( options );

        var savedHold = await verify.Holds
            .AsNoTracking()
            .SingleAsync( x =>
                x.BuyerId == buyer &&
                x.IdempotencyKey == "same-command" );

        Assert.Equal( firstHold.Id, savedHold.Id );
        Assert.Equal( HoldStatus.Active, savedHold.Status );

        var seats = await verify.Seats
            .AsNoTracking()
            .Where( x =>
                x.EventId == DemoEvent &&
                requestedCodes.Contains( x.SeatCode ) )
            .ToArrayAsync();

        Assert.Equal( requestedCodes.Length, seats.Length );

        Assert.All( seats, seat =>
        {
            Assert.Equal( SeatStatus.Held, seat.Status );
            Assert.Equal( firstHold.Id, seat.HoldId );
            Assert.Null( seat.SoldOrderId );
            Assert.Equal( firstHold.ExpiresAtUtc, seat.HeldUntilUtc );
        } );
    }

    [Fact]
    public async Task Two_concurrent_hold_requests_from_two_buyers_for_different_seats_using_the_same_idempotency_key_succeed()
    {
        await using var postgres = new PostgreSqlBuilder( "postgres:17-alpine" ).Build();

        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql( postgres.GetConnectionString() ).Options;

        await TestDatabaseInitializer.InitializeAsync( options );

        Guid firstBuyer = Guid.NewGuid();
        Guid secondBuyer = Guid.NewGuid();

        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously );

        async Task<OperationResult<HoldView>> HoldAsync(
            BookingDbContext db,
            Guid buyer,
            string[] requestedCodes )
        {
            await start.Task;

            var service = new InventoryService(
                db, TimeProvider.System );

            return await service.HoldAsync(
                DemoEvent,
                buyer,
                "buyer_same_idempotency_key",
                requestedCodes,
                CancellationToken.None );
        }

        await using var db1 = new BookingDbContext( options );
        await using var db2 = new BookingDbContext( options );

        var firstBuyerRequestedCodes = new[] { "A-001", "A-002" };
        var confirmFirstTask = HoldAsync(
            db1, firstBuyer, firstBuyerRequestedCodes );

        var secondBuyerRequestedCodes = new[] { "A-003", "A-004" };
        var confirmSecondTask = HoldAsync(
            db2, secondBuyer, secondBuyerRequestedCodes );

        // Release both asynchronous operations.
        start.SetResult( true );

        // Wait for both actual ConfirmAsync operations to finish.
        var results =
            await Task.WhenAll(
                confirmFirstTask,
                confirmSecondTask );

        Assert.All( results, result =>
        {
            Assert.Equal( ResultKind.Created, result.Kind );
            Assert.NotNull( result.Value );
        } );

        var firstHold = results[ 0 ].Value!;
        var secondHold = results[ 1 ].Value!;

        // The two buyers must receive different reservations.
        Assert.NotEqual( firstHold.Id, secondHold.Id );

        Assert.Equal( firstBuyer, firstHold.BuyerId );
        Assert.Equal( secondBuyer, secondHold.BuyerId );

        Assert.Equal( DemoEvent, firstHold.EventId );
        Assert.Equal( DemoEvent, secondHold.EventId );

        Assert.Equal(
            firstBuyerRequestedCodes,
            firstHold.SeatCodes );

        Assert.Equal(
            secondBuyerRequestedCodes,
            secondHold.SeatCodes );

        await using var verify = new BookingDbContext( options );

        var holds = await verify.Holds
            .AsNoTracking()
            .Where( x => x.EventId == DemoEvent )
            .ToArrayAsync();

        Assert.Equal( 2, holds.Length );

        // Verify that each reservation is persisted
        // with the correct buyer and idempotency key.
        foreach(var expected in new[]
        {
            (Hold: firstHold, Buyer: firstBuyer),
            (Hold: secondHold, Buyer: secondBuyer)
        })
        {
            var saved = Assert.Single(
                holds,
                x => x.Id == expected.Hold.Id );

            Assert.Equal( expected.Buyer, saved.BuyerId );
            Assert.Equal(
                "buyer_same_idempotency_key",
                saved.IdempotencyKey );

            Assert.Equal( HoldStatus.Active, saved.Status );
            Assert.Equal( expected.Hold.ExpiresAtUtc,
                         saved.ExpiresAtUtc );
        }

        // Verify individual seat ownership.
        var seats = await verify.Seats
            .AsNoTracking()
            .Where( x => x.EventId == DemoEvent )
            .ToArrayAsync();

        foreach(var expected in new[]
        {
            (Hold: firstHold, Codes: firstBuyerRequestedCodes),
            (Hold: secondHold, Codes: secondBuyerRequestedCodes)
        })
        {
            foreach(string code in expected.Codes)
            {
                var seat = Assert.Single(
                    seats,
                    x => x.SeatCode == code );

                Assert.Equal( SeatStatus.Held, seat.Status );
                Assert.Equal( expected.Hold.Id, seat.HoldId );
                Assert.Equal( expected.Hold.ExpiresAtUtc,
                             seat.HeldUntilUtc );
                Assert.Null( seat.SoldOrderId );
            }
        }

        // No checkout has taken place.
        Assert.Equal( 0, await verify.Orders.CountAsync() );
    }

    [Fact]
    public async Task Two_concurrent_hold_requests_from_same_buyer_for_different_seats_using_the_same_idempotency_key_never_overhold()
    {
        await using var postgres = new PostgreSqlBuilder( "postgres:17-alpine" ).Build();

        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql( postgres.GetConnectionString() ).Options;

        await TestDatabaseInitializer.InitializeAsync( options );

        Guid buyer = Guid.NewGuid();

        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously );

        async Task<OperationResult<HoldView>> HoldAsync(
            BookingDbContext db,
            Guid buyer,
            string[] requestedCodes )
        {
            await start.Task;

            var service = new InventoryService(
                db, TimeProvider.System );

            return await service.HoldAsync(
                DemoEvent,
                buyer,
                "buyer_same_idempotency_key",
                requestedCodes,
                CancellationToken.None );
        }

        await using var db1 = new BookingDbContext( options );
        await using var db2 = new BookingDbContext( options );

        var firstBuyerRequestedCodes = new[] { "A-001", "A-002" };
        var confirmFirstTask = HoldAsync(
            db1, buyer, firstBuyerRequestedCodes );

        var secondBuyerRequestedCodes = new[] { "A-003", "A-004" };
        var confirmSecondTask = HoldAsync(
            db2, buyer, secondBuyerRequestedCodes );

        // Release both asynchronous operations.
        start.SetResult( true );

        // Wait for both actual ConfirmAsync operations to finish.
        var results =
            await Task.WhenAll(
                confirmFirstTask,
                confirmSecondTask );

        Assert.Equal(
            1, results.Count( r => r.Kind == ResultKind.Created ) );

        Assert.Equal(
            1, results.Count( r => r.Kind == ResultKind.Conflict ) );

        var created = Assert.Single(
            results, r => r.Kind == ResultKind.Created );

        var conflict = Assert.Single(
            results, r => r.Kind == ResultKind.Conflict );

        Assert.NotNull( created.Value );
        Assert.Null( conflict.Value );

        // Either request can win the race.
        bool firstRequestSucceeded =
            results[ 0 ].Kind == ResultKind.Created;

        var winningCodes = firstRequestSucceeded
            ? firstBuyerRequestedCodes
            : secondBuyerRequestedCodes;

        var losingCodes = firstRequestSucceeded
            ? secondBuyerRequestedCodes
            : firstBuyerRequestedCodes;

        // Verify the returned reservation.
        Assert.Equal( buyer, created.Value.BuyerId );
        Assert.Equal( DemoEvent, created.Value.EventId );
        Assert.Equal( winningCodes, created.Value.SeatCodes );

        await using var verify = new BookingDbContext( options );

        // There must be exactly one persisted reservation.
        var holds = await verify.Holds
            .AsNoTracking()
            .ToArrayAsync();

        var savedHold = Assert.Single( holds );

        Assert.Equal( created.Value.Id, savedHold.Id );
        Assert.Equal( buyer, savedHold.BuyerId );
        Assert.Equal( DemoEvent, savedHold.EventId );
        Assert.Equal(
            "buyer_same_idempotency_key",
            savedHold.IdempotencyKey );

        Assert.Equal( HoldStatus.Active, savedHold.Status );
        Assert.Equal( winningCodes.Length, savedHold.SeatCount );
        Assert.Equal(
            string.Join( ",", winningCodes ),
            savedHold.SeatCodesCsv );

        Assert.Equal(
            created.Value.ExpiresAtUtc,
            savedHold.ExpiresAtUtc );

        // Retrieve all four requested seats.
        var requestedCodes = firstBuyerRequestedCodes
            .Concat( secondBuyerRequestedCodes )
            .ToArray();

        var seats = await verify.Seats
            .AsNoTracking()
            .Where( x =>
                x.EventId == DemoEvent &&
                requestedCodes.Contains( x.SeatCode ) )
            .ToDictionaryAsync( x => x.SeatCode );

        Assert.Equal( 4, seats.Count );

        // The successful request must hold exactly its own seats.
        foreach(var code in winningCodes)
        {
            var seat = seats[ code ];

            Assert.Equal( SeatStatus.Held, seat.Status );
            Assert.Equal( savedHold.Id, seat.HoldId );
            Assert.Equal(
                savedHold.ExpiresAtUtc,
                seat.HeldUntilUtc );
            Assert.Null( seat.SoldOrderId );
        }

        // The unsuccessful request must leave its seats unchanged.
        foreach(var code in losingCodes)
        {
            var seat = seats[ code ];

            Assert.Equal( SeatStatus.Available, seat.Status );
            Assert.Null( seat.HoldId );
            Assert.Null( seat.HeldUntilUtc );
            Assert.Null( seat.SoldOrderId );
        }

        // No checkout has taken place.
        Assert.Empty( await verify.Orders.ToArrayAsync() );
    }

    [Fact]
    public async Task Duplicate_idempotency_key_reuses_hold_and_different_payload_conflicts()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;

        await TestDatabaseInitializer.InitializeAsync( options );

        Guid buyer = Guid.NewGuid();

        await using var db1 = new BookingDbContext(options);
        var first = await new InventoryService(db1, TimeProvider.System)
            .HoldAsync(DemoEvent, buyer, "same-command", ["A-001"], CancellationToken.None);

        await using var db2 = new BookingDbContext(options);
        var replay = await new InventoryService(db2, TimeProvider.System)
            .HoldAsync(DemoEvent, buyer, "same-command", ["A-001"], CancellationToken.None);

        await using var db3 = new BookingDbContext(options);
        var altered = await new InventoryService(db3, TimeProvider.System)
            .HoldAsync(DemoEvent, buyer, "same-command", ["A-002"], CancellationToken.None);

        Assert.Equal(ResultKind.Created, first.Kind);
        Assert.Equal(ResultKind.Replayed, replay.Kind);
        Assert.Equal(first.Value!.Id, replay.Value!.Id);
        Assert.Equal(ResultKind.Conflict, altered.Kind);
    }
}
