using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class DeleteGuestSessionHandlerTests
{
    private const string UserId = "owner-user";
    private const string GroupId = "group-1";
    private const string SessionId = "session2";

    private readonly Mock<IGuestSessionService> _mockGuestSessionService = new();
    private readonly Mock<ILambdaContext> _mockContext = new();
    private readonly DeleteGuestSessionHandler _handler;

    public DeleteGuestSessionHandlerTests()
    {
        _mockContext.SetupGet(c => c.Logger).Returns(new Mock<ILambdaLogger>().Object);
        _handler = new DeleteGuestSessionHandler(_mockGuestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_Success_Returns200WithCamelCaseSnapshot()
    {
        _mockGuestSessionService
            .Setup(s => s.DeleteGuestSessionAsync(UserId, GroupId, SessionId))
            .ReturnsAsync(new GetAllSessionsResponse(
                [new Participant("session1", ParticipantType.Guest, "Andrii", [], 0, false)], 0));

        var response = await _handler.HandleRequest(CreateRequest(), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        var participant = json.RootElement.GetProperty("participants")[0];
        Assert.Equal("session1", participant.GetProperty("id").GetString());
        Assert.Equal("Guest", participant.GetProperty("type").GetString());
        Assert.False(participant.GetProperty("isSubmitted").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task HandleRequest_NoUserClaim_Returns401()
    {
        var response = await _handler.HandleRequest(CreateRequest(userId: null), _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        _mockGuestSessionService.Verify(
            s => s.DeleteGuestSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    public static TheoryData<Exception, int> ServiceExceptions() => new()
    {
        { new ActionForbiddenException("Only the reservation owner can perform this action."), 403 },
        { new GroupReservationNotFoundException(), 404 },
        { new ReservationNotFoundException("Parent reservation not found."), 404 },
        { new GuestSessionNotFoundException("Guest session not found."), 404 },
        { new ValidationException("Guest session does not belong to this group reservation."), 400 },
        { new ValidationException("The owner's own session cannot be removed from the group."), 400 },
        { new GroupReservationSubmittedException(), 400 },
        { new GroupReservationExpiredException(), 400 },
    };

    [Theory]
    [MemberData(nameof(ServiceExceptions))]
    public async Task HandleRequest_ServiceThrows_MapsToStatusCode(Exception exception, int expectedStatusCode)
    {
        _mockGuestSessionService
            .Setup(s => s.DeleteGuestSessionAsync(UserId, GroupId, SessionId))
            .ThrowsAsync(exception);

        var response = await _handler.HandleRequest(CreateRequest(), _mockContext.Object);

        Assert.Equal(expectedStatusCode, response.StatusCode);
    }

    private static APIGatewayProxyRequest CreateRequest(string? userId = UserId) => new()
    {
        PathParameters = new Dictionary<string, string>
        {
            ["groupReservationId"] = GroupId,
            ["guestSessionId"] = SessionId,
        },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = userId is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["sub"] = userId },
            },
        },
    };
}
