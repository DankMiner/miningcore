using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.DaemonInterface;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Ironfish
{
    public class IronfishJobManager : JobManagerBase<IronfishJob>
    {
        public IronfishJobManager(
            IComponentContext ctx,
            IMasterClock clock,
            IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider) :
            base(ctx, clock, messageBus, extraNonceProvider)
        {
        }

        private DaemonClient daemon;
        private IronfishCoinTemplate coin;
        private string poolAddress;
        private readonly Dictionary<string, IronfishJob> validJobs = new();
        private IronfishNetworkType networkType;

        protected async Task<bool> UpdateJob(CancellationToken ct, string reason = null)
        {
            try
            {
                var response = await GetBlockTemplateAsync(ct);
                
                if (response.Error != null)
                {
                    logger.Warn(() => $"Error during getblocktemplate: {response.Error.Message} [{response.Error.Code}]");
                    return false;
                }

                var blockTemplate = response.Response.ToObject<IronfishBlockTemplate>();
                
                // Create job
                var job = CreateJob(blockTemplate);
                
                lock (jobLock)
                {
                    validJobs[job.JobId] = job;
                    
                    // Remove old jobs
                    var obsoleteKeys = validJobs.Keys
                        .Where(key => validJobs[key].BlockTemplate.Height < job.BlockTemplate.Height - 8)
                        .ToArray();
                    
                    foreach (var key in obsoleteKeys)
                        validJobs.Remove(key);
                }

                BlockchainStats.LastNetworkBlockTime = clock.Now;
                BlockchainStats.BlockHeight = job.BlockTemplate.Height;
                BlockchainStats.NetworkDifficulty = job.BlockTemplate.Difficulty;
                BlockchainStats.NextNetworkTarget = job.BlockTemplate.Target;
                BlockchainStats.NextNetworkBits = job.BlockTemplate.Target;

                // Update pool stats
                messageBus.NotifyChainHeight(poolConfig.Id, job.BlockTemplate.Height, poolConfig.Template);

                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
            }

            return false;
        }

        private IronfishJob CreateJob(IronfishBlockTemplate blockTemplate)
        {
            var jobId = NextJobId("x8");
            return new IronfishJob(jobId, blockTemplate, poolAddress);
        }

        private async Task<DaemonResponse<JsonRpcResponse>> GetBlockTemplateAsync(CancellationToken ct)
        {
            var request = new JsonRpcRequest("getblocktemplate", new Dictionary<string, object>
            {
                ["address"] = poolAddress
            });

            return await daemon.ExecuteCmdSingleAsync<JsonRpcResponse>(logger, request, ct);
        }

        public override void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            coin = poolConfig.Template.As<IronfishCoinTemplate>();
            
            base.Configure(poolConfig, clusterConfig);
        }

        public override async Task<bool> ValidateAddressAsync(string address, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(address))
                return false;

            // Ironfish addresses are 64 character hex strings
            if (address.Length != 64 || !address.All(c => "0123456789abcdefABCDEF".Contains(c)))
                return false;

            return true;
        }

        protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
        {
            var request = new JsonRpcRequest("getblockchaininfo");
            var response = await daemon.ExecuteCmdAnyAsync<IronfishChainInfo>(logger, request, ct);

            return response.Error == null && response.Response?.Synced == true;
        }

        protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
        {
            var request = new JsonRpcRequest("getpeerinfo");
            var response = await daemon.ExecuteCmdAnyAsync<IronfishPeerInfo[]>(logger, request, ct);

            return response.Error == null && response.Response?.Length > 0;
        }

        protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
        {
            var syncPendingNotificationShown = false;

            while (true)
            {
                var request = new JsonRpcRequest("getblockchaininfo");
                var response = await daemon.ExecuteCmdAnyAsync<IronfishChainInfo>(logger, request, ct);

                var isSynched = response.Error == null && response.Response?.Synced == true;

                if (isSynched)
                {
                    logger.Info(() => "Daemon is synced with blockchain");
                    break;
                }

                if (!syncPendingNotificationShown)
                {
                    logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced");
                    syncPendingNotificationShown = true;
                }

                await ShowDaemonSyncProgressAsync(ct);
                await Task.Delay(5000, ct);
            }
        }

        private async Task ShowDaemonSyncProgressAsync(CancellationToken ct)
        {
            var request = new JsonRpcRequest("getblockchaininfo");
            var response = await daemon.ExecuteCmdAnyAsync<IronfishChainInfo>(logger, request, ct);

            if (response.Error == null && response.Response != null)
            {
                var blockCount = response.Response.Blocks;
                var headers = response.Response.Headers;
                var percent = blockCount * 100.0 / headers;

                logger.Info(() => $"Daemon has downloaded {percent:0.00}% of blockchain from {headers} blocks");
            }
        }

        protected override async Task PostStartInitAsync(CancellationToken ct)
        {
            // Get pool address
            poolAddress = poolConfig.Address;

            // Validate pool address
            if (!await ValidateAddressAsync(poolAddress, ct))
                throw new PoolStartupException($"Pool address {poolAddress} is not valid");

            // Get network info
            var request = new JsonRpcRequest("getnetworkinfo");
            var response = await daemon.ExecuteCmdAnyAsync<IronfishNetworkInfo>(logger, request, ct);

            if (response.Error != null)
                throw new PoolStartupException($"Error getting network info: {response.Error.Message}");

            networkType = response.Response.NetworkId switch
            {
                0 => IronfishNetworkType.Mainnet,
                1 => IronfishNetworkType.Testnet,
                _ => throw new PoolStartupException($"Unsupported network type {response.Response.NetworkId}")
            };

            // Start job updates
            SetupJobUpdates(ct);
        }

        protected override void SetupJobUpdates(CancellationToken ct)
        {
            var blockFound = blockFoundSubject.Synchronize();
            var pollInterval = poolConfig.BlockRefreshInterval > 0 ? poolConfig.BlockRefreshInterval : 1000;

            var triggers = new List<IObservable<(bool Force, string Via, string Data)>>
            {
                blockFound.Select(x => (false, JobRefreshBy.BlockFound, (string)null))
            };

            if (poolConfig.JobRebroadcastTimeout > 0)
            {
                var interval = TimeSpan.FromMilliseconds(poolConfig.JobRebroadcastTimeout);
                triggers.Add(Observable.Timer(interval, interval)
                    .Select(_ => (false, JobRefreshBy.PeriodicalRebroadcast, (string)null)));
            }

            triggers.Add(Observable.Interval(TimeSpan.FromMilliseconds(pollInterval))
                .Select(_ => (false, JobRefreshBy.Poll, (string)null))
                .TakeUntil(blockFound)
                .Repeat());

            Jobs = Observable.Merge(triggers)
                .Select(x => Observable.FromAsync(() => UpdateJob(ct, x.Via)))
                .Concat()
                .Where(x => x)
                .Do(_ => HasInitialBlockTemplate = true)
                .Select(_ => GetJobParamsForStratum())
                .Publish()
                .RefCount();
        }

        private object GetJobParamsForStratum()
        {
            lock (jobLock)
            {
                var job = validJobs.Values.LastOrDefault();
                return job?.GetJobParams();
            }
        }

        public override async Task<Share> SubmitShareAsync(StratumConnection worker, object submission, CancellationToken ct)
        {
            Contract.RequiresNonNull(worker);
            Contract.RequiresNonNull(submission);

            if (submission is not object[] submitParams)
                throw new StratumException(StratumError.Other, "invalid submission");

            var workerName = submitParams[0]?.ToString() ?? string.Empty;
            var jobId = submitParams[1]?.ToString();
            var extraNonce2 = submitParams[2]?.ToString();
            var nTime = submitParams[3]?.ToString();
            var nonce = submitParams[4]?.ToString();

            if (string.IsNullOrEmpty(jobId))
                throw new StratumException(StratumError.Other, "missing job id");

            IronfishJob job;

            lock (jobLock)
            {
                if (!validJobs.TryGetValue(jobId, out job))
                    throw new StratumException(StratumError.JobNotFound, "job not found");
            }

            var context = worker.ContextAs<IronfishWorkerContext>();
            var (share, blockHex, blockHash) = job.ProcessShare(worker, workerName, context.ExtraNonce1, extraNonce2, nTime, nonce);

            share.PoolId = poolConfig.Id;
            share.Source = clusterConfig.ClusterName;
            share.Created = clock.Now;

            if (share.IsBlockCandidate)
            {
                logger.Info(() => $"Submitting block {blockHash} [{share.BlockHeight}]");

                var submitRequest = new JsonRpcRequest("submitBlock", new[] { blockHex });
                var submitResponse = await daemon.ExecuteCmdAnyAsync<string>(logger, submitRequest, ct);

                if (submitResponse.Error != null)
                {
                    logger.Warn(() => $"Block submission failed: {submitResponse.Error.Message} [{submitResponse.Error.Code}]");
                    share.TransactionConfirmationData = submitResponse.Error.Message;
                }
                else
                {
                    share.TransactionConfirmationData = submitResponse.Response;
                    share.BlockHash = blockHash;
                    share.BlockReward = decimal.Parse(job.BlockTemplate.MinersFee);

                    logger.Info(() => $"Block {blockHash} [{share.BlockHeight}] submission successful");

                    messageBus.NotifyBlockFound(poolId, share, coin);
                    blockFoundSubject.OnNext(share.BlockHeight);
                }
            }

            return share;
        }

        #region API-Surface

        public IObservable<object> Jobs { get; private set; }

        public override void PrepareWorker(StratumConnection connection)
        {
            var context = connection.ContextAs<IronfishWorkerContext>();

            context.ExtraNonce1 = extraNonceProvider.Next();
            context.Difficulty = poolConfig.PoolPayoutSchemeConfig.MinimumDifficulty ?? 1;
        }

        public async ValueTask<object[]> GetSubscriberDataAsync(StratumConnection connection)
        {
            var context = connection.ContextAs<IronfishWorkerContext>();

            var data = new object[]
            {
                new object[]
                {
                    new object[] { "mining.notify", connection.ConnectionId },
                    new object[] { "mining.set_difficulty", connection.ConnectionId }
                },
                context.ExtraNonce1,
                IronfishConstants.ExtraNonceSize
            };

            return await Task.FromResult(data);
        }

        public async ValueTask<Share> SubmitShareAsync(StratumConnection connection, object submission)
        {
            return await SubmitShareAsync(connection, submission, CancellationToken.None);
        }

        public double? GetStaticDifficulty(StratumConnection connection)
        {
            return null;
        }

        #endregion
    }

    public class IronfishWorkerContext : WorkerContextBase
    {
        public string ExtraNonce1 { get; set; }
    }

    public enum IronfishNetworkType
    {
        Mainnet = 0,
        Testnet = 1
    }

    public class IronfishChainInfo
    {
        public int Blocks { get; set; }
        public int Headers { get; set; }
        public bool Synced { get; set; }
    }

    public class IronfishNetworkInfo
    {
        public int NetworkId { get; set; }
    }

    public class IronfishPeerInfo
    {
        public string Addr { get; set; }
        public int Version { get; set; }
    }

    public class IronfishCoinTemplate : CoinTemplate
    {
    }
}
