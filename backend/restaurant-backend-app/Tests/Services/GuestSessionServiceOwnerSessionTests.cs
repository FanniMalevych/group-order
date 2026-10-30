using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using Moq;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceOwnerSessionTests
{
    private const string OwnerId = "owner-user";
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";

    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly GuestSessionService _guestSessionService;

    private readonly GroupReservation _group = new()
    {
        GroupReservationId = GroupId,
        ReservationId = ReservationId,
        ReservationToken = "token",
        Status = GroupReservationStatus.Open,
    };

    public GuestSessionServiceOwnerSessionTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            new Mock<IDishRepository>().Object,
            _mockReservationRepository.Object,
            new Mock<IUserRepository>().Object,
            new Mock<ITableRepository>().Object);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation { Id = ReservationId, TableId = "table-1", WaiterId = "waiter-1", CustomerId = OwnerId });

        _mockGroupReservationRepository
            .Setup(r => r.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync(_group);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_OwnerSessionExists_ReturnsItWithoutSaving()
    {
        SetupSessions(Session("guest-session", isOwner: false), Session("owner-session", isOwner: true));

        var result = await _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId);

        Assert.Equal("owner-session", result.GuestSessionId);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_NoOwnerSession_CreatesAndReturnsIt()
    {
        SetupSessions(Session("guest-session", isOwner: false));

        GuestSession? saved = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => saved = s);

        var result = await _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId);

        Assert.NotNull(saved);
        Assert.Equal(saved.Id, result.GuestSessionId);
        Assert.True(saved.IsOwnerSession);
        Assert.Equal(OwnerId, saved.OwnerUserId);
        Assert.Equal(GroupId, saved.GroupReservationId);
        Assert.Empty(saved.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Once);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_ReservationNotFound_ThrowsReservationNotFoundException()
    {
        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, "missing-reservation"));
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_CallerIsNotOwner_ThrowsActionForbiddenException()
    {
        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync("someone-else", ReservationId));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_GroupNotFound_ThrowsGroupReservationNotFoundException()
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId));
    }

    [Theory]
    [InlineData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task GetOwnerGuestSessionIdAsync_NoOwnerSessionAndGroupNotOpen_ThrowsWithoutCreating(
        GroupReservationStatus status, Type exceptionType)
    {
        _group.Status = status;
        SetupSessions();

        await Assert.ThrowsAsync(exceptionType,
            () => _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_GroupSubmittedButOwnerSessionExists_ReturnsIt()
    {
        _group.Status = GroupReservationStatus.Submitted;
        SetupSessions(Session("owner-session", isOwner: true));

        var result = await _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId);

        Assert.Equal("owner-session", result.GuestSessionId);
    }

    private void SetupSessions(params GuestSession[] sessions)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync(sessions.ToList());
    }

    private static GuestSession Session(string id, bool isOwner) => new()
    {
        Id = id,
        GroupReservationId = GroupId,
        GuestName = isOwner ? "Owner" : "Guest",
        IsOwnerSession = isOwner,
        Dishes = [],
    };
}
