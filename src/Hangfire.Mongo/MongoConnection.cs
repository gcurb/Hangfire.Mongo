﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hangfire.Common;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.DistributedLock;
using Hangfire.Mongo.Dto;
using Hangfire.Server;
using Hangfire.Storage;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hangfire.Mongo
{
    /// <summary>
    /// MongoDB database connection for Hangfire
    /// </summary>
    internal class MongoConnection : JobStorageConnection
    {
        private readonly MongoStorageOptions _storageOptions;
        private readonly MongoJobFetcher _jobFetcher;
        
        private readonly HangfireDbContext _dbContext;
        
#pragma warning disable 1591
        public MongoConnection(
            HangfireDbContext database,
            MongoStorageOptions storageOptions,
            IJobQueueSemaphore jobQueueSemaphore)
        {
            _dbContext = database ?? throw new ArgumentNullException(nameof(database));
            _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
            _jobFetcher = new MongoJobFetcher(database, storageOptions, jobQueueSemaphore);
        }

        public override IWriteOnlyTransaction CreateWriteTransaction()
        {
            return new MongoWriteOnlyTransaction(_dbContext);
        }

        public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
        {
            return new MongoDistributedLock($"Hangfire:{resource}", timeout, _dbContext, _storageOptions, DistributedLockMutex.Instance);
        }

        public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt,
            TimeSpan expireIn)
        {
            string jobId;
            using (var transaction = new MongoWriteOnlyTransaction(_dbContext))
            {
                jobId = transaction.CreateExpiredJob(job, parameters, createdAt, expireIn);
                transaction.Commit();
            }

            return jobId;
        }

        public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null || queues.Length == 0)
            {
                throw new ArgumentNullException(nameof(queues));
            }

            return _jobFetcher.FetchNextJob(queues, cancellationToken);
        }

        public override void SetJobParameter(string id, string name, string value)
        {
            using (var transaction = new MongoWriteOnlyTransaction(_dbContext))
            {
                transaction.SetJobParameter(id, name, value);
                transaction.Commit();
            }
        }

        public override string GetJobParameter(string id, string name)
        {
            if (id == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            var parameters = _dbContext
                .JobGraph
                .OfType<JobDto>()
                .Find(j => j.Id == ObjectId.Parse(id))
                .Project(job => job.Parameters)
                .FirstOrDefault();

            string value = null;
            parameters?.TryGetValue(name, out value);

            return value;
        }

        public override JobData GetJobData(string jobId)
        {
            if (jobId == null)
            {
                throw new ArgumentNullException(nameof(jobId));
            }

            var jobData = _dbContext
                .JobGraph
                .OfType<JobDto>()
                .Find(Builders<JobDto>.Filter.Eq(_ => _.Id, ObjectId.Parse(jobId)))
                .FirstOrDefault();

            if (jobData == null)
            {
                return null;
            }

            // TODO: conversion exception could be thrown.
            var invocationData = JobHelper.FromJson<InvocationData>(jobData.InvocationData);
            invocationData.Arguments = jobData.Arguments;

            Job job = null;
            JobLoadException loadException = null;

            try
            {
                job = invocationData.Deserialize();
            }
            catch (JobLoadException ex)
            {
                loadException = ex;
            }

            return new JobData
            {
                Job = job,
                State = jobData.StateName,
                CreatedAt = jobData.CreatedAt,
                LoadException = loadException
            };
        }

        public override StateData GetStateData(string jobId)
        {
            if (jobId == null)
            {
                throw new ArgumentNullException(nameof(jobId));
            }
            
            var job = _dbContext
                .JobGraph
                .OfType<JobDto>()
                .Find(j => j.Id == ObjectId.Parse(jobId))
                .FirstOrDefault();

            if (job == null)
            {
                return null;
            }

            var state = job.StateHistory.LastOrDefault();

            if (state == null)
            {
                return null;
            }

            return new StateData
            {
                Name = state.Name,
                Reason = state.Reason,
                Data = state.Data
            };
        }

        public override void AnnounceServer(string serverId, ServerContext context)
        {
            if (serverId == null)
            {
                throw new ArgumentNullException(nameof(serverId));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var set = new BsonDocument("$set", new BsonDocument
            {
                [nameof(ServerDto.WorkerCount)] = context.WorkerCount,
                [nameof(ServerDto.Queues)] = new BsonArray(context.Queues),
                [nameof(ServerDto.StartedAt)] = DateTime.UtcNow,
                [nameof(ServerDto.LastHeartbeat)] = DateTime.UtcNow
            });

            _dbContext.Server.UpdateOne(new BsonDocument("_id", serverId), set, new UpdateOptions {IsUpsert = true});
        }

        public override void RemoveServer(string serverId)
        {
            if (serverId == null)
            {
                throw new ArgumentNullException(nameof(serverId));
            }

            _dbContext.Server.DeleteMany(Builders<ServerDto>.Filter.Eq(_ => _.Id, serverId));
        }

        public override void Heartbeat(string serverId)
        {
            if (serverId == null)
            {
                throw new ArgumentNullException(nameof(serverId));
            }

            _dbContext.Server.UpdateMany(Builders<ServerDto>.Filter.Eq(_ => _.Id, serverId),
                Builders<ServerDto>.Update.Set(_ => _.LastHeartbeat, DateTime.UtcNow));
        }

        public override int RemoveTimedOutServers(TimeSpan timeOut)
        {
            if (timeOut.Duration() != timeOut)
            {
                throw new ArgumentException("The `timeOut` value must be positive.", nameof(timeOut));
            }

            return (int)_dbContext
                .Server
                .DeleteMany(Builders<ServerDto>.Filter.Lt(_ => _.LastHeartbeat, DateTime.UtcNow.Add(timeOut.Negate())))
                .DeletedCount;
        }

        public override HashSet<string> GetAllItemsFromSet(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var result = _dbContext
                .JobGraph
                .OfType<SetDto>()
                .Find(Builders<SetDto>.Filter.Regex(_ => _.Key, $"^{key}"))
                .SortBy(_ => _.Id)
                .Project(_ => _.Value)
                .ToList();

            
            return new HashSet<string>(result);
        }

        public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (toScore < fromScore)
            {
                throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.");
            }
            
            return _dbContext
                .JobGraph
                .OfType<SetDto>()
                .Find(Builders<SetDto>.Filter.Regex(_ => _.Key, $"^{key}") &
                      Builders<SetDto>.Filter.Gte(_ => _.Score, fromScore) &
                      Builders<SetDto>.Filter.Lte(_ => _.Score, toScore))
                .SortBy(_ => _.Score)
                .Project(_ => _.Value)
                .FirstOrDefault();
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            using (var transaction = new MongoWriteOnlyTransaction(_dbContext))
            {
                transaction.SetRangeInHash(key, keyValuePairs);
                transaction.Commit();
            }
        }

        public override Dictionary<string, string> GetAllEntriesFromHash(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var hash = _dbContext
                .JobGraph
                .OfType<HashDto>()
                .Find(new BsonDocument(nameof(KeyJobDto.Key), key))
                .FirstOrDefault();

            return hash?.Fields;
        }

        public override long GetSetCount(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return _dbContext
                .JobGraph
                .OfType<SetDto>()
                .Find(Builders<SetDto>.Filter.Regex(_ => _.Key, $"^{key}"))
                .Count();
        }

        public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return _dbContext
                .JobGraph
                .OfType<SetDto>()
                .Find(Builders<SetDto>.Filter.Regex(_ => _.Key, $"^{key}"))
                .SortBy(_ => _.Id)
                .Skip(startingFrom)
                .Limit(endingAt - startingFrom + 1) // inclusive -- ensure the last element is included
                .Project(dto => dto.Value)
                .ToList();
        }

        public override TimeSpan GetSetTtl(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var values = _dbContext
                .JobGraph
                .OfType<SetDto>()
                .Find(Builders<SetDto>.Filter.Regex(_ => _.Key, $"^{key}") &
                      Builders<SetDto>.Filter.Not(Builders<SetDto>.Filter.Eq(_ => _.ExpireAt, null)))
                .Project(dto => dto.ExpireAt.Value)
                .ToList();

            return values.Any() ? values.Min() - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        public override long GetCounter(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var counter = _dbContext
                .JobGraph
                .OfType<CounterDto>()
                .Find(new BsonDocument(nameof(KeyJobDto.Key), key))
                .FirstOrDefault();

            return counter?.Value ?? 0;
        }

        public override long GetHashCount(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var hash = _dbContext
                .JobGraph
                .OfType<HashDto>()
                .Find(new BsonDocument(nameof(KeyJobDto.Key), key))
                .FirstOrDefault();

            return hash?.Fields.Count ?? 0;
        }

        public override TimeSpan GetHashTtl(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var result = _dbContext
                .JobGraph
                .OfType<HashDto>()
                .Find(Builders<HashDto>.Filter.Eq(_ => _.Key, key))
                .SortBy(dto => dto.ExpireAt)
                .Project(_ => _.ExpireAt)
                .FirstOrDefault();

            return result.HasValue ? result.Value - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        public override string GetValueFromHash(string key, string name)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            var hashWithField = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument($"{nameof(HashDto.Fields)}.{name}", new BsonDocument("$exists", true))
            });
            
            var result = _dbContext
                .JobGraph
                .OfType<HashDto>()
                .Find(hashWithField)
                .FirstOrDefault();

            return result?.Fields[name];
        }

        public override long GetListCount(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return _dbContext
                .JobGraph
                .OfType<ListDto>()
                .Find(new BsonDocument(nameof(ListDto.Item), key))
                .Count();
        }

        public override TimeSpan GetListTtl(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var result = _dbContext
                .JobGraph
                .OfType<ListDto>()
                .Find(new BsonDocument(nameof(ListDto.Item), key))
                .SortBy(_ => _.ExpireAt)
                .Project(_ => _.ExpireAt)
                .FirstOrDefault();
                
            return result.HasValue ? result.Value - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return _dbContext
                .JobGraph
                .OfType<ListDto>()
                .Find(new BsonDocument(nameof(ListDto.Item), key))
                .SortByDescending(_ => _.Id)
                .Skip(startingFrom)
                .Limit(endingAt - startingFrom + 1) // inclusive -- ensure the last element is included
                .Project(_ => _.Value)
                .ToList();
        }

        public override List<string> GetAllItemsFromList(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return _dbContext
                .JobGraph
                .OfType<ListDto>()
                .Find(new BsonDocument(nameof(ListDto.Item), key))
                .SortByDescending(_ => _.Id)
                .Project(_ => _.Value)
                .ToList();
        }
    }
#pragma warning restore 1591
}