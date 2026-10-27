using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.Dishes;
using APIHandler.Models.GuestSessions;
using APIHandler.Models.Orders;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class GetGroupSessionsHandlerTests
{
    private const string UserId = "owner-user";
    private const string GroupId = "group-1";

    private readonly Mock<IGuestSessionService> _mockGuestSessionService = new();
    private readonly Mock<ILambdaContext> _mockContext = new();
    private readonly GetGroupSessionsHandler _handler;

    public GetGroupSessionsHandlerTests()
    {
        _mockContext.SetupGet(c => c.Logger).Returns(new Mock<ILambdaLogger>().Object);
        _handler = new GetGroupSessionsHandler(_mockGuestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_Success_Returns200WithCamelCaseSnapshot()
    {
        var item = new OrderItemResponse
        {
            Dish = new DishResponse
            {
                Id = "dish1", Name = "Caesar Salad", Price = 180, Description = "", DishType = "", Weight = "",
                Calories = "", Proteins = "", Fats = "", Carbohydrates = "", Vitamins = "", ImageUrl = null, State = "",
            },
            Quantity = 2,
        };
        _mockGuestSessionService
            .Setup(s => s.GetGroupSessionsAsync(UserId, GroupId))
            .ReturnsAsync(new GetAllSessionsResponse(
                [new Participant("session1", ParticipantType.Guest, "Andrii", [item], 360, true)], 360));

        var response = await _handler.HandleRequest(CreateRequest(), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        var participant = json.RootElement.GetProperty("participants")[0];
        Assert.Equal("session1", participant.GetProperty("id").GetString());
        Assert.Equal("Guest", participant.GetProperty("type").GetString());
        Assert.Equal("Andrii", participant.GetProperty("name").GetString());
        Assert.Equal(180, participant.GetProperty("items")[0].GetProperty("dish").GetProperty("price").GetDecimal());
        Assert.Equal(2, participant.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(360, participant.GetProperty("total").GetDecimal());
        Assert.True(participant.GetProperty("isSubmitted").GetBoolean());
        Assert.Equal(360, json.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task HandleRequest_NoUserClaim_Returns401()
    {
        var response = await _handler.HandleRequest(CreateRequest(userId: null), _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        _mockGuestSessionService.Verify(s => s.GetGroupSessionsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_MissingGroupId_Returns400()
    {
        var request = CreateRequest();
        request.PathParameters = new Dictionary<string, string>();

        var response = await _handler.HandleRequest(request, _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
    }

    public static TheoryData<Exception, int> ServiceExceptions() => new()
    {
        { new ActionForbiddenException("Only the reservation owner can perform this action."), 403 },
        { new GroupReservationNotFoundException(), 404 },
        { new ReservationNotFoundException("Parent reservation not found."), 404 },
        { new ValidationException("Something is invalid."), 400 },
    };

    [Theory]
    [MemberData(nameof(ServiceExceptions))]
    public async Task HandleRequest_ServiceThrows_MapsToStatusCode(Exception exception, int expectedStatusCode)
    {
        _mockGuestSessionService
            .Setup(s => s.GetGroupSessionsAsync(UserId, GroupId))
            .ThrowsAsync(exception);

        var response = await _handler.HandleRequest(CreateRequest(), _mockContext.Object);

        Assert.Equal(expectedStatusCode, response.StatusCode);
    }

    private static APIGatewayProxyRequest CreateRequest(string? userId = UserId) => new()
    {
        PathParameters = new Dictionary<string, string> { ["groupReservationId"] = GroupId },
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
