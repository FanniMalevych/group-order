using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class GetOwnerGuestSessionIdHandlerTests
{
    private const string UserId = "owner-user";
    private const string ReservationId = "reservation-1";

    private readonly Mock<IGuestSessionService> _mockGuestSessionService = new();
    private readonly Mock<ILambdaContext> _mockContext = new();
    private readonly GetOwnerGuestSessionIdHandler _handler;

    public GetOwnerGuestSessionIdHandlerTests()
    {
        _mockContext.SetupGet(c => c.Logger).Returns(new Mock<ILambdaLogger>().Object);
        _handler = new GetOwnerGuestSessionIdHandler(_mockGuestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_Success_Returns200WithGuestSessionId()
    {
        _mockGuestSessionService
            .Setup(s => s.GetOwnerGuestSessionIdAsync(UserId, ReservationId))
            .ReturnsAsync(new OwnerGuestSessionIdResponse("owner-session-123"));

        var response = await _handler.HandleRequest(CreateRequest(), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal("owner-session-123", json.RootElement.GetProperty("guestSessionId").GetString());
    }

    [Fact]
    public async Task HandleRequest_NoUserClaim_Returns401()
    {
        var response = await _handler.HandleRequest(CreateRequest(userId: null), _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        _mockGuestSessionService.Verify(s => s.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    public static TheoryData<Exception, int> ServiceExceptions() => new()
    {
        { new ActionForbiddenException("Only the reservation owner can perform this action."), 403 },
        { new ReservationNotFoundException("Reservation was not found."), 404 },
        { new GroupReservationNotFoundException(), 404 },
        { new GroupReservationSubmittedException(), 400 },
    };

    [Theory]
    [MemberData(nameof(ServiceExceptions))]
    public async Task HandleRequest_ServiceThrows_MapsToStatusCode(Exception exception, int expectedStatusCode)
    {
        _mockGuestSessionService
            .Setup(s => s.GetOwnerGuestSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(exception);

        var response = await _handler.HandleRequest(CreateRequest(), _mockContext.Object);

        Assert.Equal(expectedStatusCode, response.StatusCode);
    }

    private static APIGatewayProxyRequest CreateRequest(string? userId = UserId) => new()
    {
        PathParameters = new Dictionary<string, string> { ["reservationId"] = ReservationId },
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
