using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.Auth;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.Locations;
using Moq;

namespace APIHandler.Tests.Services;

public class GroupReservationServiceSubmitTests
{
    private const string OwnerId = "owner-user";
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";

    private readonly Mock<IGroupReservationRepository> _groupReservationRepositoryMock = new();
    private readonly Mock<IReservationRepository> _reservationRepositoryMock = new();
    private readonly Mock<IGuestSessionRepository> _guestSessionRepositoryMock = new();
    private readonly Mock<IOrderRepository> _orderRepositoryMock = new();
    private readonly GroupReservationService _service;

    private readonly GroupReservation _group = new()
    {
        GroupReservationId = GroupId,
        ReservationId = ReservationId,
        ReservationToken = "token",
        Status = GroupReservationStatus.Open,
    };

    private readonly Order _order = new()
    {
        OrderId = "order-1",
        CustomerId = OwnerId,
        ReservationId = ReservationId,
        ReservationAddress = "Some street 19, Table 1",
        ReservationTime = "Oct 14, 2026, 9:00 a.m. - 10:00 a.m.",
        Dishes = [],
        Status = OrderStatus.Created,
    };

    public GroupReservationServiceSubmitTests()
    {
        _service = new GroupReservationService(
            _groupReservationRepositoryMock.Object,
            new Mock<ITableRepository>().Object,
            _reservationRepositoryMock.Object,
            _guestSessionRepositoryMock.Object,
            _orderRepositoryMock.Object,
            new Mock<IUserService>().Object,
            new Mock<ILocationService>().Object);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation { Id = ReservationId, TableId = "table-1", WaiterId = "waiter-1", CustomerId = OwnerId });

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(_group);

        _orderRepositoryMock
            .Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync(_order);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_SubmittedSessions_GroupsDishesByIdAndSumsQuantities()
    {
        SetupSessions(
            Session("andrii", submitted: true, ("dish1", 2), ("dish2", 1)),
            Session("olena", submitted: true, ("dish1", 1), ("dish2", 1)));

        var result = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal(
            [new SubmittedDishResponse("dish1", 3), new SubmittedDishResponse("dish2", 2)],
            result);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_UnsubmittedSessions_AreIgnored()
    {
        SetupSessions(
            Session("andrii", submitted: true, ("dish1", 2)),
            Session("olena", submitted: false, ("dish1", 10), ("dish3", 5)));

        var result = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal([new SubmittedDishResponse("dish1", 2)], result);
        Assert.DoesNotContain(_order.Dishes, d => d.DishId == "dish3");
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OwnerSessionSubmitted_IsIncluded()
    {
        var owner = Session("owner", submitted: true, ("dish2", 1));
        owner.IsOwnerSession = true;
        SetupSessions(owner, Session("andrii", submitted: true, ("dish2", 2)));

        var result = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal([new SubmittedDishResponse("dish2", 3)], result);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_Success_AddsDishesToOrderAndUpdatesBothStatusesInOneWrite()
    {
        SetupSessions(Session("andrii", submitted: true, ("dish1", 2), ("dish2", 1)));

        await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal(OrderStatus.SubmittedByCustomer, _order.Status);
        Assert.Equal(GroupReservationStatus.Submitted, _group.Status);
        Assert.Collection(_order.Dishes,
            d => { Assert.Equal("dish1", d.DishId); Assert.Equal(2, d.Quantity); Assert.Equal(OrderItemState.Pending, d.State); },
            d => { Assert.Equal("dish2", d.DishId); Assert.Equal(1, d.Quantity); Assert.Equal(OrderItemState.Pending, d.State); });

        _groupReservationRepositoryMock.Verify(r => r.SaveGroupReservationWithOrderAsync(_group, _order), Times.Once);
        _groupReservationRepositoryMock.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
        _orderRepositoryMock.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OrderAlreadyHasDish_MergesQuantitiesWithoutDuplicates()
    {
        _order.Dishes.Add(new OrderItem { DishId = "dish1", Quantity = 1, State = OrderItemState.Confirmed });
        SetupSessions(Session("andrii", submitted: true, ("dish1", 2), ("dish2", 1)));

        var result = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal(2, _order.Dishes.Count);
        var dish1 = Assert.Single(_order.Dishes, d => d.DishId == "dish1");
        Assert.Equal(3, dish1.Quantity);
        Assert.Equal(OrderItemState.Confirmed, dish1.State);
        Assert.Equal(1, Assert.Single(_order.Dishes, d => d.DishId == "dish2").Quantity);

        // The response lists what the guests submitted, not the merged order.
        Assert.Equal([new SubmittedDishResponse("dish1", 2), new SubmittedDishResponse("dish2", 1)], result);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_ReservationNotFound_ThrowsReservationNotFoundException()
    {
        await AssertThrowsWithoutSaving<ReservationNotFoundException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, "missing-reservation"));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_CallerIsNotOwner_ThrowsActionForbiddenException()
    {
        await AssertThrowsWithoutSaving<ActionForbiddenException>(
            () => _service.SubmitGroupPreOrderAsync("someone-else", GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupNotFound_ThrowsGroupReservationNotFoundException()
    {
        await AssertThrowsWithoutSaving<GroupReservationNotFoundException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, "missing-group", ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupBelongsToAnotherReservation_ThrowsValidationException()
    {
        _group.ReservationId = "another-reservation";

        await AssertThrowsWithoutSaving<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupAlreadySubmitted_ThrowsGroupReservationSubmittedException()
    {
        _group.Status = GroupReservationStatus.Submitted;

        await AssertThrowsWithoutSaving<GroupReservationSubmittedException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupClosed_ThrowsGroupReservationExpiredException()
    {
        _group.Status = GroupReservationStatus.Closed;

        await AssertThrowsWithoutSaving<GroupReservationExpiredException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_NoSubmittedSessions_ThrowsValidationException()
    {
        SetupSessions(Session("andrii", submitted: false, ("dish1", 2)));

        var exception = await AssertThrowsWithoutSaving<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        Assert.Contains("submitted", exception.Message);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_NoSessionsAtAll_ThrowsValidationException()
    {
        SetupSessions();

        await AssertThrowsWithoutSaving<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_SubmittedSessionsHaveNoDishes_ThrowsValidationException()
    {
        SetupSessions(
            Session("andrii", submitted: true),
            Session("olena", submitted: true, ("dish1", 0)));

        var exception = await AssertThrowsWithoutSaving<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        Assert.Contains("no dishes", exception.Message);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OrderNotFound_ThrowsValidationException()
    {
        SetupSessions(Session("andrii", submitted: true, ("dish1", 2)));
        _orderRepositoryMock
            .Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync((Order?)null);

        var exception = await AssertThrowsWithoutSaving<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        Assert.Contains("not found", exception.Message);
        Assert.Equal(GroupReservationStatus.Open, _group.Status);
    }

    private async Task<T> AssertThrowsWithoutSaving<T>(Func<Task> action) where T : Exception
    {
        var exception = await Assert.ThrowsAsync<T>(action);

        _groupReservationRepositoryMock.Verify(
            r => r.SaveGroupReservationWithOrderAsync(It.IsAny<GroupReservation>(), It.IsAny<Order>()), Times.Never);

        return exception;
    }

    private void SetupSessions(params GuestSession[] sessions)
    {
        _guestSessionRepositoryMock
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync(sessions.ToList());
    }

    private static GuestSession Session(string id, bool submitted, params (string DishId, int Quantity)[] dishes) => new()
    {
        Id = id,
        GroupReservationId = GroupId,
        GuestName = id,
        IsSubmitted = submitted,
        Dishes = dishes.Select(d => new OrderItem { DishId = d.DishId, Quantity = d.Quantity }).ToList(),
    };
}
