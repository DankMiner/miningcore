using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.DaemonInterface;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;

namespace Miningcore.Blockchain.Quai
{
    public class QuaiJobManager : JobManagerBase<QuaiJob>
    {
        public QuaiJobManager(
            IComponentContext ctx,
            IMasterClock clock,
            IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider) : 
            base(ctx, clock, messageBus, extraNonceProvider)
        {
        }
        
        private DaemonClient daemon;
        private DaemonClient stratumProxy;
        private IHashAlgorithm progpowHasher;
        private QuaiCoinTemplate coin;
        
        public override void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            Contract.RequiresNonNull(poolConfig);
            Contract.RequiresNonNull(clusterConfig);
            
            this.poolConfig = poolConfig;
            this.clusterConfig = clusterConfig;
            
            // Configure daemons
            ConfigureDaemons();
            
            // Initialize hasher
            progpowHasher = ctx.Resolve<IHashAlgorithm>(
                new TypedParameter(typeof(string), "progpow-quai"));
        }
        
        private void ConfigureDaemons()
        {
            // Primary daemon is go-quai node
            daemon = new DaemonClient(jsonSerializerSettings, messageBus, clock, logger);
            daemon.Configure(poolConfig.Daemons.First());
            
            // Secondary daemon is go-quai-stratum proxy
            if (poolConfig.Daemons.Length > 1)
            {
                stratumProxy = new DaemonClient(jsonSerializerSettings, messageBus, clock, logger);
                stratumProxy.Configure(poolConfig.Daemons[1]);
            }
        }
        
        protected override async Task<bool> UpdateJob(CancellationToken ct, 
            string via = null, string json = null)
        {
            try
            {
                // Get work from stratum proxy or directly from node
                var response = await GetWorkAsync(ct);
                
                if (response?.Error != null)
                {
                    logger.Warn(() => $"Failed to update job: {response.Error.Message}");
                    return false;
                }
                
                var work = response.Response;
                var job = CreateJob(work);
                
                lock (jobLock)
                {
                    validJobs.Insert(0, job);
                    
                    // Trim old jobs
                    while (validJobs.Count > maxActiveJobs)
                        validJobs.RemoveAt(validJobs.Count - 1);
                    
                    currentJob = job;
                }
                
                // Notify workers
                await messageBus.SendAsync(new NewJobNotification
                {
                    PoolId = poolConfig.Id,
                    JobId = job.JobId
                });
                
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return false;
            }
        }
        
        private async Task<DaemonResponse<QuaiWork>> GetWorkAsync(CancellationToken ct)
        {
            // Try stratum proxy first if available
            if (stratumProxy != null)
            {
                return await stratumProxy.ExecuteCmdSingleAsync<QuaiWork>(
                    logger, "quai_getWork", ct);
            }
            
            // Otherwise get directly from node
            return await daemon.ExecuteCmdSingleAsync<QuaiWork>(
                logger, "quai_getWork", ct);
        }
        
        private QuaiJob CreateJob(QuaiWork work)
        {
            var job = new QuaiJob(progpowHasher)
            {
                JobId = NextJobId(),
                BlockTemplate = new QuaiBlockTemplate
                {
                    Height = work.Number,
                    PreviousBlockHash = work.ParentHash,
                    Target = work.Target,
                    HeaderHash = work.HeaderHash,
                    MixHash = work.MixHash
                },
                PreviousBlockHash = work.ParentHash,
                Target = new uint256(work.Target),
                Difficulty = CalculateDifficulty(work.Target),
                RegionNumber = work.Location.Region,
                ZoneNumber = work.Location.Zone,
                ChainId = $"{work.Location.Region}-{work.Location.Zone}"
            };
            
            return job;
        }
        
        private double CalculateDifficulty(string targetHex)
        {
            // Calculate difficulty from target
            var target = new uint256(targetHex);
            var difficulty = new uint256("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff") / target;
            return difficulty.GetLow64();
        }
        
        public override async Task<Share> SubmitShareAsync(
            StratumClient client, 
            object submission,
            CancellationToken ct)
        {
            Contract.RequiresNonNull(client);
            Contract.RequiresNonNull(submission);
            
            var context = client.ContextAs<QuaiWorkerContext>();
            var submitParams = submission as object[];
            
            if (submitParams?.Length != 3)
                throw new StratumException(StratumError.Other, "Invalid submission");
            
            var jobId = submitParams[0] as string;
            var nonce = submitParams[1] as string;
            var mixHash = submitParams[2] as string;
            
            // Find job
            QuaiJob job;
            lock (jobLock)
            {
                job = validJobs.FirstOrDefault(x => x.JobId == jobId);
            }
            
            if (job == null)
                throw new StratumException(StratumError.JobNotFound, "Job not found");
            
            // Process share
            var share = job.ProcessShare(nonce, context.Worker, client.RemoteEndpoint.Address.ToString());
            share.PoolId = poolConfig.Id;
            share.Source = client.ConnectionId;
            
            // Submit to daemon if block candidate
            if (share.IsBlockCandidate)
            {
                await SubmitBlockAsync(share, ct);
            }
            
            return share;
        }
        
        private async Task SubmitBlockAsync(QuaiShare share, CancellationToken ct)
        {
            // Submit block to appropriate chain (region/zone)
            var submitParams = new[]
            {
                share.Nonce,
                share.HeaderHash,
                share.MixHash
            };
            
            var response = await daemon.ExecuteCmdSingleAsync<string>(
                logger, "quai_submitWork", ct, submitParams);
            
            if (response?.Response == "true")
            {
                logger.Info(() => $"Block found at height {share.BlockHeight} on chain {share.ChainId}");
                
                // Publish block notification
                await messageBus.SendAsync(new BlockFoundNotification
                {
                    PoolId = poolConfig.Id,
                    BlockHeight = share.BlockHeight,
                    Symbol = poolConfig.Coin.Type
                });
            }
        }
    }
}
