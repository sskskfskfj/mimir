using System.Text;
using Libplanet.Crypto;
using Mimir.Worker.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Mimir.Worker.Services;

public class MongoDbStore
{
    private readonly ILogger<MongoDbStore> _logger;

    private readonly IMongoClient _client;

    private readonly IMongoDatabase _database;

    private readonly string _databaseName;

    private readonly GridFSBucket _gridFs;

    private IMongoCollection<BsonDocument> ArenaCollection =>
        _database.GetCollection<BsonDocument>("arena");

    private IMongoCollection<BsonDocument> AvatarCollection =>
        _database.GetCollection<BsonDocument>("avatars");

    private IMongoCollection<BsonDocument> MetadataCollection =>
        _database.GetCollection<BsonDocument>("metadata");

    private IMongoCollection<BsonDocument> TableSheetsCollection =>
        _database.GetCollection<BsonDocument>("tableSheets");

    public MongoDbStore(ILogger<MongoDbStore> logger, string connectionString, string databaseName)
    {
        _client = new MongoClient(connectionString);
        _database = _client.GetDatabase(databaseName);
        _logger = logger;
        _databaseName = databaseName;
        _gridFs = new GridFSBucket(_database);
    }

    public async Task LinkAvatarWithArenaAsync(Address address)
    {
        var avatarFilter = Builders<BsonDocument>.Filter.Eq("Avatar.address", address.ToHex());
        var avatar = await AvatarCollection.Find(avatarFilter).FirstOrDefaultAsync();
        if (avatar != null)
        {
            var objectId = avatar["_id"].AsObjectId;
            var arenaFilter = Builders<BsonDocument>.Filter.Eq("AvatarAddress", address.ToHex());
            var update = Builders<BsonDocument>.Update.Set("AvatarObjectId", objectId);
            var updateModel = new UpdateOneModel<BsonDocument>(arenaFilter, update)
            {
                IsUpsert = false
            };
            await ArenaCollection.BulkWriteAsync(
                new List<WriteModel<BsonDocument>> { updateModel }
            );
        }
    }

    public async Task UpdateLatestBlockIndex(long blockIndex)
    {
        _logger.LogInformation($"Update latest block index to {blockIndex}");
        var filter = Builders<BsonDocument>.Filter.Eq("_id", "SyncContext");
        var update = Builders<BsonDocument>.Update.Set("LatestBlockIndex", blockIndex);
        
        var updateModel = new UpdateOneModel<BsonDocument>(filter, update);
        var response = await MetadataCollection.BulkWriteAsync(new[] { updateModel });
        if (response.ModifiedCount < 1)
        {
            await MetadataCollection.InsertOneAsync(
                new SyncContext
                {
                    LatestBlockIndex = blockIndex,
                }.ToBsonDocument());
        }
    }

    public async Task<long> GetLatestBlockIndex()
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", "SyncContext");
        var doc = await MetadataCollection.FindSync(filter).FirstAsync();
        return doc.GetValue("LatestBlockIndex").AsInt64;
    }

    public async Task BulkUpsertArenaDataAsync(List<ArenaData> arenaDatas)
    {
        var bulkOps = new List<WriteModel<BsonDocument>>();

        try
        {
            foreach (var arenaData in arenaDatas)
            {
                var filter = Builders<BsonDocument>.Filter.Eq(
                    "AvatarAddress",
                    arenaData.AvatarAddress.ToHex()
                );
                var bsonDocument = BsonDocument.Parse(arenaData.ToJson());
                var upsertOne = new ReplaceOneModel<BsonDocument>(filter, bsonDocument)
                {
                    IsUpsert = true
                };
                bulkOps.Add(upsertOne);
            }
            if (bulkOps.Count > 0)
            {
                using var session = await _database.Client.StartSessionAsync();
                await ArenaCollection.BulkWriteAsync(bulkOps);
            }

            _logger.LogInformation($"Stored {bulkOps.Count} arena data");
        }
        catch (Exception ex)
        {
            _logger.LogError($"An error occurred during BulkUpsertArenaDataAsync: {ex.Message}");
        }
    }

    public async Task BulkUpsertAvatarDataAsync(List<AvatarData> avatarDatas)
    {
        var bulkOps = new List<WriteModel<BsonDocument>>();

        try
        {
            foreach (var avatarData in avatarDatas)
            {
                var filter = Builders<BsonDocument>.Filter.Eq(
                    "Avatar.address",
                    avatarData.Avatar.address.ToHex()
                );
                var bsonDocument = BsonDocument.Parse(avatarData.ToJson());
                var upsertOne = new ReplaceOneModel<BsonDocument>(filter, bsonDocument)
                {
                    IsUpsert = true
                };
                bulkOps.Add(upsertOne);
            }
            if (bulkOps.Count > 0)
            {
                await AvatarCollection.BulkWriteAsync(bulkOps);
            }

            _logger.LogInformation($"Stored {bulkOps.Count} avatar data");
        }
        catch (Exception ex)
        {
            _logger.LogError($"An error occurred during BulkUpsertAvatarDataAsync: {ex.Message}");
        }
    }

    public async Task InsertTableSheets(TableSheetData sheetData)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("Address", sheetData.Address.ToHex());

        var sheetCsvBytes = Encoding.UTF8.GetBytes(sheetData.SheetCsv);
        var sheetCsvId = await _gridFs.UploadFromBytesAsync($"{sheetData.Name}-csv", sheetCsvBytes);
        var sheetRawBytes = Encoding.UTF8.GetBytes(sheetData.Raw);
        var sheetRawId = await _gridFs.UploadFromBytesAsync($"{sheetData.Name}-raw", sheetRawBytes);

        var document = BsonDocument.Parse(sheetData.ToJson());

        document.Remove("SheetCsv");
        document.Add("SheetCsvFileId", sheetCsvId);
        document.Remove("Raw");
        document.Add("SheetRawFileId", sheetRawId);
        
        await TableSheetsCollection.ReplaceOneAsync(
            filter,
            document,
            new ReplaceOptions { IsUpsert = true }
        );
    }

    public async Task<bool> IsInitialized()
    {
        var names = await (
            await _client.GetDatabase(_databaseName).ListCollectionNamesAsync()
        ).ToListAsync();
        return names is not { } ns || !(ns.Contains("arena") && ns.Contains("avatars"));
    }
}
