using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using Moq;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceGroupSessionsTests
{
    private const string OwnerId = "owner-user";
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";

    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly GuestSessionService _guestSessionService;

    private readonly Dish _caesar = new() { Id = "dish1", Name = "Caesar Salad", Price = 180 };
    private readonly Dish _tomYum = new() { Id = "dish2", Name = "Tom Yum", Price = 220 };

    public GuestSessionServiceGroupSessionsTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = GroupId,
                ReservationId = ReservationId,
                ReservationToken = "token",
                Status = GroupReservationStatus.Open,
            });

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = ReservationId,
                TableId = "table-1",
                WaiterId = "waiter-1",
                CustomerId = OwnerId,
            });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((IEnumerable<string> ids) => new[] { _caesar, _tomYum }.Where(d => ids.Contains(d.Id)).ToList());
    }

    [Fact]
    public async Task GetGroupSessionsAsync_Guests_ReturnsItemsAndTotals()
    {
        SetupSessions(
            Guest("session1", "Andrii", submitted: true, ("dish1", 2)),
            Guest("session2", "Olena", submitted: false, ("dish2", 1)));

        GetAllSessionsResponse result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        var participants = result.Participants.ToList();
        Assert.Equal(2, participants.Count);

        Assert.Equal("session1", participants[0].Id);
        Assert.Equal(ParticipantType.Guest, participants[0].Type);
        Assert.Equal("Andrii", participants[0].Name);
        Assert.True(participants[0].IsSubmitted);
        Assert.Equal(360, participants[0].Total);
        var item = Assert.Single(participants[0].Items);
        Assert.Equal("dish1", item.Dish.Id);
        Assert.Equal("Caesar Salad", item.Dish.Name);
        Assert.Equal(180, item.Dish.Price);
        Assert.Equal(2, item.Quantity);

        Assert.False(participants[1].IsSubmitted);
        Assert.Equal(220, participants[1].Total);

        Assert.Equal(580, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_ParticipantWithSeveralDishes_SumsPriceTimesQuantity()
    {
        SetupSessions(Guest("session1", "Andrii", submitted: false, ("dish1", 3), ("dish2", 2)));

        GetAllSessionsResponse result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        // 180 * 3 + 220 * 2
        Assert.Equal(980, Assert.Single(result.Participants).Total);
        Assert.Equal(980, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OwnerSession_ListedFirstAsOwnerWithCognitoName()
    {
        var owner = Guest("owner-session", "Owner", submitted: true, ("dish2", 1));
        owner.IsOwnerSession = true;
        owner.OwnerUserId = OwnerId;
        SetupSessions(Guest("session1", "Andrii", submitted: true, ("dish1", 1)), owner);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(OwnerId))
            .ReturnsAsync(new UserType { Attributes = [new AttributeType { Name = "given_name", Value = "Fanni" }] });

        GetAllSessionsResponse result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        var first = result.Participants.First();
        Assert.Equal("owner-session", first.Id);
        Assert.Equal(ParticipantType.Owner, first.Type);
        Assert.Equal("Fanni", first.Name);
        Assert.Equal(400, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OwnerUserNotFound_FallsBackToSessionName()
    {
        var owner = Guest("owner-session", "Owner", submitted: false);
        owner.IsOwnerSession = true;
        SetupSessions(owner);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(OwnerId))
            .ReturnsAsync((UserType?)null);

        GetAllSessionsResponse result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        Assert.Equal("Owner", Assert.Single(result.Participants).Name);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_NoOwnerSession_DoesNotLookUpUser()
    {
        SetupSessions(Guest("session1", "Andrii", submitted: false));

        await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        _mockUserRepository.Verify(r => r.GetUserByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_DishNoLongerExists_SkipsItem()
    {
        SetupSessions(Guest("session1", "Andrii", submitted: false, ("dish1", 1), ("deleted-dish", 5)));

        GetAllSessionsResponse result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        var participant = Assert.Single(result.Participants);
        Assert.Equal("dish1", Assert.Single(participant.Items).Dish.Id);
        Assert.Equal(180, participant.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_NoSessions_ReturnsEmptyListAndZeroTotal()
    {
        SetupSessions();

        GetAllSessionsResponse result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        Assert.Empty(result.Participants);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_LoadsAllDishesInOneBatch()
    {
        SetupSessions(
            Guest("session1", "Andrii", submitted: true, ("dish1", 1), ("dish2", 1)),
            Guest("session2", "Olena", submitted: true, ("dish1", 2)));

        await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        _mockDishRepository.Verify(
            r => r.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { "dish1", "dish2" }))),
            Times.Once);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_GroupNotFound_ThrowsGroupReservationNotFoundException()
    {
        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync(OwnerId, "missing-group"));
    }

    [Fact]
    public async Task GetGroupSessionsAsync_ReservationNotFound_ThrowsReservationNotFoundException()
    {
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId));
    }

    [Fact]
    public async Task GetGroupSessionsAsync_CallerIsNotOwner_ThrowsActionForbiddenException()
    {
        SetupSessions();

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetGroupSessionsAsync("someone-else", GroupId));

        _mockGuestSessionRepository.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    private void SetupSessions(params GuestSession[] sessions)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync(sessions.ToList());
    }

    private static GuestSession Guest(string id, string name, bool submitted, params (string DishId, int Quantity)[] dishes)
        => new()
        {
            Id = id,
            GroupReservationId = GroupId,
            GuestName = name,
            IsSubmitted = submitted,
            Dishes = dishes.Select(d => new OrderItem { DishId = d.DishId, Quantity = d.Quantity }).ToList(),
        };
}
