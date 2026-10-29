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

public class GuestSessionServiceDeleteSessionTests
{
    private const string OwnerId = "owner-user";
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";

    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly GuestSessionService _guestSessionService;

    private readonly Dish _caesar = new() { Id = "dish1", Name = "Caesar Salad", Price = 180 };
    private readonly Dish _tomYum = new() { Id = "dish2", Name = "Tom Yum", Price = 220 };

    private readonly GroupReservation _group = new()
    {
        GroupReservationId = GroupId,
        ReservationId = ReservationId,
        ReservationToken = "token",
        Status = GroupReservationStatus.Open,
    };

    private readonly GuestSession _owner;
    private readonly GuestSession _andrii;
    private readonly GuestSession _olena;

    public GuestSessionServiceDeleteSessionTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            new Mock<IUserRepository>().Object,
            new Mock<ITableRepository>().Object);

        _owner = Session("owner-session", "Owner", ("dish2", 1));
        _owner.IsOwnerSession = true;
        _andrii = Session("session1", "Andrii", ("dish1", 2));
        _olena = Session("session2", "Olena", ("dish2", 1));

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(_group);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation { Id = ReservationId, TableId = "table-1", WaiterId = "waiter-1", CustomerId = OwnerId });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((IEnumerable<string> ids) => new[] { _caesar, _tomYum }.Where(d => ids.Contains(d.Id)).ToList());

        foreach (var session in new[] { _owner, _andrii, _olena })
        {
            _mockGuestSessionRepository
                .Setup(r => r.GetByGuestSessionId(session.Id))
                .ReturnsAsync(session);
        }

        // Simulates the eventually consistent index still returning the deleted session.
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync(() => [_owner, _andrii, _olena]);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_GuestSession_DeletesAndReturnsUpdatedSnapshot()
    {
        GetAllSessionsResponse result = await _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "session2");

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(_olena), Times.Once);

        var participants = result.Participants.ToList();
        Assert.Equal(["owner-session", "session1"], participants.Select(p => p.Id));
        Assert.Equal(ParticipantType.Owner, participants[0].Type);
        Assert.Equal(ParticipantType.Guest, participants[1].Type);
        Assert.Equal(220, participants[0].Total);
        Assert.Equal(360, participants[1].Total);
        Assert.Equal(580, result.Total);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_CallerIsNotOwner_ThrowsActionForbiddenException()
    {
        await AssertThrowsWithoutDeleting<ActionForbiddenException>(
            () => _guestSessionService.DeleteGuestSessionAsync("someone-else", GroupId, "session2"));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_GroupNotFound_ThrowsGroupReservationNotFoundException()
    {
        await AssertThrowsWithoutDeleting<GroupReservationNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync(OwnerId, "missing-group", "session2"));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SessionNotFound_ThrowsGuestSessionNotFoundException()
    {
        await AssertThrowsWithoutDeleting<GuestSessionNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "missing-session"));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SessionFromAnotherGroup_ThrowsValidationException()
    {
        _olena.GroupReservationId = "another-group";

        await AssertThrowsWithoutDeleting<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "session2"));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_OwnerSession_ThrowsValidationException()
    {
        var exception = await AssertThrowsWithoutDeleting<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "owner-session"));

        Assert.Contains("owner", exception.Message);
    }

    [Theory]
    [InlineData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task DeleteGuestSessionAsync_GroupNotOpen_ThrowsWithoutDeleting(GroupReservationStatus status, Type exceptionType)
    {
        _group.Status = status;

        await Assert.ThrowsAsync(exceptionType,
            () => _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "session2"));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SubmittedGuestInOpenGroup_IsDeleted()
    {
        _olena.IsSubmitted = true;

        await _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "session2");

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(_olena), Times.Once);
    }

    private async Task<T> AssertThrowsWithoutDeleting<T>(Func<Task> action) where T : Exception
    {
        var exception = await Assert.ThrowsAsync<T>(action);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
        return exception;
    }

    private static GuestSession Session(string id, string name, params (string DishId, int Quantity)[] dishes) => new()
    {
        Id = id,
        GroupReservationId = GroupId,
        GuestName = name,
        Dishes = dishes.Select(d => new OrderItem { DishId = d.DishId, Quantity = d.Quantity }).ToList(),
    };
}
