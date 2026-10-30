using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GroupReservations;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.GroupReservations;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class SubmitGroupPreOrderHandlerTests
{
    private const string UserId = "owner-user";
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";

    private readonly Mock<IGroupReservationService> _mockService = new();
    private readonly Mock<ILambdaContext> _mockContext = new();
    private readonly SubmitGroupPreOrderHandler _handler;

    public SubmitGroupPreOrderHandlerTests()
    {
        _mockContext.SetupGet(c => c.Logger).Returns(new Mock<ILambdaLogger>().Object);
        _handler = new SubmitGroupPreOrderHandler(_mockService.Object);
    }

    [Fact]
    public async Task HandleRequest_Success_Returns200WithCamelCaseDishList()
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ReturnsAsync([new SubmittedDishResponse("dish1", 3), new SubmittedDishResponse("dish2", 2)]);

        var response = await _handler.HandleRequest(CreateRequest(), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        var items = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("dish1", items[0].GetProperty("dishId").GetString());
        Assert.Equal(3, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal("dish2", items[1].GetProperty("dishId").GetString());
        Assert.Equal(2, items[1].GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task HandleRequest_NoUserClaim_Returns401()
    {
        var response = await _handler.HandleRequest(CreateRequest(userId: null), _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    public async Task HandleRequest_InvalidBody_Returns400(string? body)
    {
        var response = await _handler.HandleRequest(CreateRequest(body: body), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    public static TheoryData<Exception, int> ServiceExceptions() => new()
    {
        { new ActionForbiddenException("Only the reservation owner can perform this action."), 403 },
        { new ReservationNotFoundException("Reservation was not found."), 404 },
        { new GroupReservationNotFoundException(), 404 },
        { new GroupReservationSubmittedException(), 409 },
        { new GroupReservationExpiredException(), 409 },
        { new ValidationException("No participant has submitted their pre-order yet."), 400 },
    };

    [Theory]
    [MemberData(nameof(ServiceExceptions))]
    public async Task HandleRequest_ServiceThrows_MapsToStatusCode(Exception exception, int expectedStatusCode)
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ThrowsAsync(exception);

        var response = await _handler.HandleRequest(CreateRequest(), _mockContext.Object);

        Assert.Equal(expectedStatusCode, response.StatusCode);
    }

    private void VerifyServiceNotCalled() =>
        _mockService.Verify(
            s => s.SubmitGroupPreOrderAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

    private static APIGatewayProxyRequest CreateRequest(
        string? userId = UserId,
        string? body = $$"""{ "reservationId": "{{ReservationId}}" }""") => new()
    {
        PathParameters = new Dictionary<string, string> { ["groupReservationId"] = GroupId },
        Body = body,
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
