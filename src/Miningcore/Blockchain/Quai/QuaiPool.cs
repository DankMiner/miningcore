using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;

namespace Miningcore.Blockchain.Quai
{
    public class QuaiPool : PoolBase
    {
        public QuaiPool(IComponentContext ctx,
            JsonSerializerSettings serializerSettings,
            IConnectionFactory cf,
            IStatsRepository statsRepo,
            IMapper mapper,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus)
        {
        }
        
        private QuaiJobManager jobManager;
        
        protected override async Task SetupJobManagerAsync(CancellationToken ct)
        {
            jobManager = ctx.Resolve<QuaiJobManager>(
                new TypedParameter(typeof(IExtraNonceProvider), extraNonceProvider));
            
            jobManager.Configure(poolConfig, clusterConfig);
            
            await jobManager.StartAsync(ct);
            
            // Subscribe to job updates
            disposables.Add(jobManager.Jobs
                .Select(job => Observable.FromAsync(async () =>
                {
                    await OnNewJobAsync(job);
                }))
                .Concat()
                .Subscribe());
            
            // Periodically update job
            disposables.Add(Observable.Interval(TimeSpan.FromSeconds(5))
                .Select(_ => Observable.FromAsync(async () =>
                {
                    await jobManager.UpdateJob(ct);
                }))
                .Concat()
                .Subscribe());
        }
        
        private async Task OnNewJobAsync(object job)
        {
            if (job is QuaiJob quaiJob)
            {
                logger.Info(() => $"New job {quaiJob.JobId} for chain {quaiJob.ChainId} " +
                    $"at height {quaiJob.BlockTemplate.Height}");
                
                // Broadcast job to all connected miners
                await BroadcastJobAsync(quaiJob);
            }
        }
        
        private async Task BroadcastJobAsync(QuaiJob job)
        {
            var jobParams = new object[]
            {
                job.JobId,
                job.PreviousBlockHash,
                job.BlockTemplate.HeaderHash,
                job.BlockTemplate.MixHash,
                job.Target.ToString(),
                true // clean jobs
            };
            
            await ForEachMinerAsync(async (client, ct) =>
            {
                await client.NotifyAsync("mining.notify", jobParams);
            });
        }
        
        protected override async Task OnRequestAsync(StratumClient client,
            Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
        {
            var request = tsRequest.Value;
            
            switch (request.Method)
            {
                case "mining.subscribe":
                    await OnSubscribeAsync(client, request, ct);
                    break;
                    
                case "mining.authorize":
                    await OnAuthorizeAsync(client, request, ct);
                    break;
                    
                case "mining.submit":
                    await OnSubmitAsync(client, request, tsRequest.Timestamp, ct);
                    break;
                    
                case "mining.multi_version":
                    // Quai specific - handle chain selection
                    await OnMultiVersionAsync(client, request, ct);
                    break;
                    
                default:
                    logger.Debug(() => $"Unsupported method {request.Method}");
                    await client.RespondErrorAsync(request.Id, StratumError.Other, 
                        $"Unsupported method {request.Method}");
                    break;
            }
        }
        
        private async Task OnSubscribeAsync(StratumClient client, 
            JsonRpcRequest request, CancellationToken ct)
        {
            var context = client.ContextAs<QuaiWorkerContext>();
            
            // Generate extra nonce
            var extraNonce1 = extraNonceProvider.Next();
            
            // Setup worker context
            context.ExtraNonce1 = extraNonce1;
            context.IsSubscribed = true;
            
            // Respond
            var response = new object[]
            {
                new[] { new[] { "mining.notify", client.ConnectionId } },
                extraNonce1,
                4 // extra nonce 2 size
            };
            
            await client.RespondAsync(response, request.Id);
            
            // Send initial job
            if (jobManager.CurrentJob != null)
            {
                await client.NotifyAsync("mining.set_difficulty", 
                    new[] { jobManager.CurrentJob.Difficulty });
                await BroadcastJobAsync(jobManager.CurrentJob);
            }
        }
        
        private async Task OnAuthorizeAsync(StratumClient client,
            JsonRpcRequest request, CancellationToken ct)
        {
            var context = client.ContextAs<QuaiWorkerContext>();
            var requestParams = request.ParamsAs<string[]>();
            
            if (requestParams?.Length < 1)
            {
                await client.RespondErrorAsync(request.Id, StratumError.MinusOne, 
                    "Invalid parameters");
                return;
            }
            
            var workerName = requestParams[0];
            var password = requestParams.Length > 1 ? requestParams[1] : null;
            
            // Validate worker name (Quai address)
            if (!IsValidQuaiAddress(workerName))
            {
                await client.RespondErrorAsync(request.Id, StratumError.MinusOne,
                    "Invalid Quai address");
                return;
            }
            
            // Setup context
            context.IsAuthorized = true;
            context.Worker = workerName;
            
            // Parse chain preference from password field if provided
            if (!string.IsNullOrEmpty(password))
            {
                ParseChainPreference(context, password);
            }
            
            await client.RespondAsync(true, request.Id);
        }
        
        private bool IsValidQuaiAddress(string address)
        {
            // Implement Quai address validation
            // Quai addresses start with "0x" and are 42 characters long
            return !string.IsNullOrEmpty(address) && 
                   address.StartsWith("0x") && 
                   address.Length == 42;
        }
        
        private void ParseChainPreference(QuaiWorkerContext context, string password)
        {
            // Parse chain preference from password field
            // Format: "x,region:zone" or "x,r:z"
            var parts = password.Split(',');
            if (parts.Length > 1)
            {
                var chainPref = parts[1].Split(':');
                if (chainPref.Length == 2)
                {
                    if (int.TryParse(chainPref[0], out var region) &&
                        int.TryParse(chainPref[1], out var zone))
                    {
                        context.PreferredRegion = region;
                        context.PreferredZone = zone;
                    }
                }
            }
        }
        
        private async Task OnSubmitAsync(StratumClient client,
            JsonRpcRequest request, DateTime timestamp, CancellationToken ct)
        {
            var context = client.ContextAs<QuaiWorkerContext>();
            
            if (!context.IsAuthorized)
            {
                await client.RespondErrorAsync(request.Id, StratumError.UnauthorizedWorker,
                    "Unauthorized worker");
                return;
            }
            
            try
            {
                var share = await jobManager.SubmitShareAsync(client, 
                    request.Params, ct);
                
                // Record share
                await shareRepo.InsertAsync(share);
                
                // Update stats
                await UpdateStatsAsync(client, share);
                
                // Respond
                await client.RespondAsync(true, request.Id);
            }
            catch (StratumException ex)
            {
                await client.RespondErrorAsync(request.Id, ex.Code, ex.Message);
            }
        }
        
        private async Task OnMultiVersionAsync(StratumClient client,
            JsonRpcRequest request, CancellationToken ct)
        {
            // Handle Quai's multi-chain version selection
            var context = client.ContextAs<QuaiWorkerContext>();
            var requestParams = request.ParamsAs<int[]>();
            
            if (requestParams?.Length >= 2)
            {
                context.PreferredRegion = requestParams[0];
                context.PreferredZone = requestParams[1];
            }
            
            await client.RespondAsync(true, request.Id);
        }
    }
}
