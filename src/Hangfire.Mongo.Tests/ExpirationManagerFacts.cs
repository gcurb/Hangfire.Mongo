using System;
using System.Collections.Generic;
using System.Threading;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.Tests.Utils;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Hangfire.Mongo.Tests
{
#pragma warning disable 1591
    [Collection("Database")]
    public class ExpirationManagerFacts
    {
        private readonly HangfireDbContext _dbContext;
        private readonly CancellationToken _token;

        public ExpirationManagerFacts()
        {
            _dbContext = ConnectionUtils.CreateDbContext();

            _token = new CancellationToken(true);
        }

        [Fact, CleanDatabase]
        public void Execute_RemovesOutdatedRecords()
        {
            CreateExpirationEntries(_dbContext, DateTime.UtcNow.AddMonths(-1));
            var manager = CreateManager();

            manager.Execute(_token);

            Assert.True(IsEntryExpired(_dbContext));
        }

        [Fact, CleanDatabase]
        public void Execute_DoesNotRemoveEntries_WithNoExpirationTimeSet()
        {
            CreateExpirationEntries(_dbContext, null);
            var manager = CreateManager();

            manager.Execute(_token);

            Assert.False(IsEntryExpired(_dbContext));
        }

        [Fact, CleanDatabase]
        public void Execute_DoesNotRemoveEntries_WithFreshExpirationTime()
        {
            CreateExpirationEntries(_dbContext, DateTime.UtcNow.AddMonths(1));
            var manager = CreateManager();

            manager.Execute(_token);


            Assert.False(IsEntryExpired(_dbContext));
        }

        [Fact, CleanDatabase]
        public void Execute_Processes_CounterTable()
        {
            // Arrange
            _dbContext.JobGraph.InsertOne(new CounterDto
            {
                Id = ObjectId.GenerateNewId(),
                Key = "key",
                Value = 1L,
                ExpireAt = DateTime.UtcNow.AddMonths(-1)
            });

            var manager = CreateManager();

            // Act
            manager.Execute(_token);

            // Assert
            var count = _dbContext.JobGraph.OfType<CounterDto>().Count(new BsonDocument());
            Assert.Equal(0, count);
        }

        [Fact, CleanDatabase]
        public void Execute_Processes_JobTable()
        {
            // Arrange
            _dbContext.JobGraph.InsertOne(new JobDto
            {
                Id = ObjectId.GenerateNewId(),
                InvocationData = "",
                Arguments = "",
                CreatedAt = DateTime.UtcNow,
                ExpireAt = DateTime.UtcNow.AddMonths(-1),
            });

            var manager = CreateManager();

            // Act
            manager.Execute(_token);

            // Assert
            var count = _dbContext.JobGraph.OfType<JobDto>().Count(new BsonDocument());
            Assert.Equal(0, count);
        }

        [Fact, CleanDatabase]
        public void Execute_Processes_ListTable()
        {
            // Arrange
            _dbContext.JobGraph.InsertOne(new ListDto
            {
                Id = ObjectId.GenerateNewId(),
                Item = "key",
                ExpireAt = DateTime.UtcNow.AddMonths(-1)
            });

            var manager = CreateManager();

            // Act
            manager.Execute(_token);

            // Assert
            var count = _dbContext
                .JobGraph
                .OfType<ListDto>()
                .Count(new BsonDocument());
            Assert.Equal(0, count);
        }

        [Fact, CleanDatabase]
        public void Execute_Processes_SetTable()
        {
            // Arrange
            _dbContext.JobGraph.InsertOne(new SetDto
            {
                Id = ObjectId.GenerateNewId(),
                Key = "key<>",
                Score = 0,
                ExpireAt = DateTime.UtcNow.AddMonths(-1)
            });

            var manager = CreateManager();

            // Act
            manager.Execute(_token);

            // Assert
            var count = _dbContext
                .JobGraph
                .OfType<SetDto>()
                .Count(new BsonDocument());
            Assert.Equal(0, count);
        }

        [Fact, CleanDatabase]
        public void Execute_Processes_HashTable()
        {
            // Arrange
            _dbContext.JobGraph.InsertOne(new HashDto
            {
                Id = ObjectId.GenerateNewId(),
                Key = "key",
                Fields = new Dictionary<string, string> {["field"] = ""},
                ExpireAt = DateTime.UtcNow.AddMonths(-1)
            });

            var manager = CreateManager();

            // Act
            manager.Execute(_token);

            // Assert
            var count = _dbContext
                .JobGraph
                .OfType<HashDto>()
                .Count(new BsonDocument());
            Assert.Equal(0, count);
        }


        [Fact, CleanDatabase]
        public void Execute_Processes_AggregatedCounterTable()
        {
            // Arrange
            _dbContext.JobGraph.InsertOne(new CounterDto
            {
                Key = "key",
                Value = 1,
                ExpireAt = DateTime.UtcNow.AddMonths(-1)
            });

            var manager = CreateManager();

            // Act
            manager.Execute(_token);

            // Assert
            Assert.Equal(0, _dbContext
                .JobGraph
                .OfType<CounterDto>()
                .Find(new BsonDocument()).Count());
        }



        private static void CreateExpirationEntries(HangfireDbContext connection, DateTime? expireAt)
        {
            Commit(connection, x => x.AddToSet("my-key", "my-value"));
            Commit(connection, x => x.AddToSet("my-key", "my-value1"));
            Commit(connection, x => x.SetRangeInHash("my-hash-key", new[] { new KeyValuePair<string, string>("key", "value"), new KeyValuePair<string, string>("key1", "value1") }));
            Commit(connection, x => x.AddRangeToSet("my-key", new[] { "my-value", "my-value1" }));

            if (expireAt.HasValue)
            {
                var expireIn = expireAt.Value - DateTime.UtcNow;
                Commit(connection, x => x.ExpireHash("my-hash-key", expireIn));
                Commit(connection, x => x.ExpireSet("my-key", expireIn));
            }
        }

        private static bool IsEntryExpired(HangfireDbContext connection)
        {
            var count = connection
                .JobGraph
                .OfType<ExpiringJobDto>()
                .Count(new BsonDocument());

            return count == 0;
        }

        private MongoExpirationManager CreateManager()
        {
            return new MongoExpirationManager(_dbContext);
        }

        private static void Commit(HangfireDbContext connection, Action<MongoWriteOnlyTransaction> action)
        {
            using (MongoWriteOnlyTransaction transaction = new MongoWriteOnlyTransaction(connection))
            {
                action(transaction);
                transaction.Commit();
            }
        }
    }
#pragma warning restore 1591
}