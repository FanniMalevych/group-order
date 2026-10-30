using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GroupReservations;

public class GroupReservationRepository : IGroupReservationRepository
{
    private static readonly string GroupReservationsTableName = ResourceNames.GroupReservationsTableName;
    private readonly IDynamoDBContext _dynamoDbContext;

    public GroupReservationRepository(IDynamoDBContext dynamoDbContext)
    {
        _dynamoDbContext = dynamoDbContext ?? throw new ArgumentNullException(nameof(dynamoDbContext));
    }

    public async Task<GroupReservation?> GetGroupReservationByIdAsync(string groupReservationId)
    {
        return await _dynamoDbContext.LoadAsync<GroupReservation>(groupReservationId, DynamoConfig.LoadConfig(GroupReservationsTableName));
    }

    public async Task DeleteGroupReservationAsync(GroupReservation groupReservation)
    {
        await _dynamoDbContext.DeleteAsync(groupReservation, DynamoConfig.DeleteConfig(GroupReservationsTableName));
    }

    public async Task SaveGroupReservationAsync(GroupReservation groupReservation)
    {
        await _dynamoDbContext.SaveAsync(groupReservation, DynamoConfig.SaveConfig(GroupReservationsTableName));
    }

    public async Task SaveGroupReservationWithOrderAsync(GroupReservation groupReservation, Order order)
    {
        var groupWrite = _dynamoDbContext.CreateTransactWrite<GroupReservation>(
            new TransactWriteConfig { OverrideTableName = GroupReservationsTableName });
        groupWrite.AddSaveItem(groupReservation);

        var orderWrite = _dynamoDbContext.CreateTransactWrite<Order>(
            new TransactWriteConfig { OverrideTableName = ResourceNames.OrdersTableName });
        orderWrite.AddSaveItem(order);

        // Both items are written in one DynamoDB transaction: all or nothing.
        // The group's version check makes a second concurrent submit fail instead of adding the dishes twice.
        try
        {
            await _dynamoDbContext.CreateMultiTableTransactWrite(groupWrite, orderWrite).ExecuteAsync();
        }
        catch (TransactionCanceledException exception)
            when (exception.CancellationReasons?.Any(r => r.Code == "ConditionalCheckFailed") == true)
        {
            throw new ConcurrentModificationException(
                $"Group reservation {groupReservation.GroupReservationId} was changed by another request.", exception);
        }
    }

    public async Task<GroupReservation?> GetGroupReservationByTokenAsync(string token)
    {
        var results = await _dynamoDbContext.QueryAsync<GroupReservation?>(token, new QueryConfig
        {
            OverrideTableName = GroupReservationsTableName,
            IndexName = GlobalSecondaryIndexes.GroupReservation.ReservationToken,
        }).GetRemainingAsync();

        return results.FirstOrDefault();
    }

    public async Task<GroupReservation?> GetByReservationIdAsync(string reservationId)
    {
        var results = await _dynamoDbContext.QueryAsync<GroupReservation?>(reservationId, new QueryConfig
        {
            OverrideTableName = GroupReservationsTableName,
            IndexName = GlobalSecondaryIndexes.GroupReservation.ReservationId,
        }).GetRemainingAsync();

        return results.FirstOrDefault();
    }
}
