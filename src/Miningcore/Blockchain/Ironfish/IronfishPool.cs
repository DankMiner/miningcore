using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using NLog;

namespace Miningcore.Blockchain.Ironfish
{
    [CoinFamily(CoinFamily.Ironfish)]
    public class IronfishPool : PoolBase
    {
        public IronfishPool(IComponentContext ctx,
            JsonSerializerSettings serializerSettings,
            IConnectionFactory cf,
            IStatsRepository statsRepo,
            IMapper mapper,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus)
        {
        }

        protected IronfishJobManager manager;
        protected IronfishCoinTemplate coin;

        protected override async Task SetupJobManager(CancellationToken ct)
        {
            manager = ctx.Resolve<IronfishJobManager>(
                new TypedParameter(typeof(IExtraNonceProvider), new IronfishExtraNonceProvider()));

            manager.Configure(poolConfig, clusterConfig);

            await manager.StartAsync(ct);

            if (poolConfig.EnableInternalStratum == true)
            {
                disposables.Add(manager.Jobs
                    .Select(job => Observable.FromAsync(async () =>
                    {
                        try
                        {
                            await OnNewJobAsync(job);
                        }
                        catch (Exception ex)
                        {
                            logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}");
                        }
                    }))
                    .Concat()
                    .Subscribe(_ => { }, ex =>
                    {
                        logger.Debug(ex, nameof(OnNewJobAsync));
                    }));

                await manager.Jobs.Take(1).ToTask(ct);
            }
        }

        protected override async Task InitStatsAsync(CancellationToken ct)
        {
            await base.InitStatsAsync(ct);

            blockchainStats = manager.BlockchainStats;
            networkStats = manager.NetworkStats;
        }

        protected override WorkerContextBase CreateWorkerContext()
        {
            return new IronfishWorkerContext();
        }

        protected override async Task OnRequestAsync(StratumConnection connection,
            Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
        {
            var request = tsRequest.Value;

            try
            {
                switch (request.Method)
                {
                    case IronfishStratumMethods.Subscribe:
                        await OnSubscribeAsync(connection, tsRequest);
                        break;

                    case IronfishStratumMethods.Authorize:
                        await OnAuthorizeAsync(connection, tsRequest, ct);
                        break;

                    case IronfishStratumMethods.SubmitShare:
                        await OnSubmitAsync(connection, tsRequest, ct);
                        break;

                    case IronfishStratumMethods.ExtraNonceSubscribe:
                        await OnExtraNonceSubscribeAsync(connection, tsRequest);
                        break;

                    default:
                        logger.Debug(() => $"[{connection.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                        await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                        break;
                }
            }
            catch (StratumException ex)
            {
                await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
            }
        }

        protected override async Task<(Share Share, string BlockHex)> OnSubmitShareAsync(
            StratumConnection connection, JsonRpcRequest request, CancellationToken ct)
        {
            Contract.RequiresNonNull(connection);
            Contract.RequiresNonNull(request);

            var context = connection.ContextAs<IronfishWorkerContext>();
            var submitRequest = request.ParamsAs<string[]>();

            if (submitRequest.Length < 5 || submitRequest.Any(string.IsNullOrEmpty))
                throw new StratumException(StratumError.MinusOne, "invalid params");

            // Extract params
            var workerName = submitRequest[0];
            var jobId = submitRequest[1];
            var extraNonce2 = submitRequest[2];
            var nTime = submitRequest[3];
            var nonce = submitRequest[4];

            // Submit share
            var share = await manager.SubmitShareAsync(connection, new object[]
            {
                workerName,
                jobId,
                extraNonce2,
                nTime,
                nonce
            }, ct);

            await connection.RespondAsync(true, request.Id);

            messageBus.SendMessage(new StratumShare(connection, share));

            return (share, null);
        }

        private async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            if (request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var context = connection.ContextAs<IronfishWorkerContext>();
            var requestParams = request.ParamsAs<string[]>();

            var data = await manager.GetSubscriberDataAsync(connection);

            await connection.RespondAsync(data, request.Id);

            context.IsSubscribed = true;
        }

        private async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest,
            CancellationToken ct)
        {
            var request = tsRequest.Value;

            if (request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var context = connection.ContextAs<IronfishWorkerContext>();
            var requestParams = request.ParamsAs<string[]>();
            var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
            var password = requestParams?.Length > 1 ? requestParams[1] : null;

            // Extract worker name and miner address
            var split = workerValue?.Split('.');
            var minerAddress = split?.FirstOrDefault()?.Trim();
            var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

            // Validate address
            if (string.IsNullOrEmpty(minerAddress))
                throw new StratumException(StratumError.MinusOne, "missing miner address");

            if (!await manager.ValidateAddressAsync(minerAddress, ct))
                throw new StratumException(StratumError.MinusOne, "invalid miner address");

            // Respond
            await connection.RespondAsync(true, request.Id);

            // Set worker context
            context.IsAuthorized = true;
            context.MinerName = minerAddress;
            context.WorkerName = workerName;

            // Send initial difficulty
            await connection.NotifyAsync(IronfishStratumMethods.SetDifficulty, new object[] { context.Difficulty });

            // Send initial job
            await SendJob(connection);

            logger.Info(() => $"[{connection.ConnectionId}] Authorized miner {minerAddress}.{workerName}");
        }

        private async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest,
            CancellationToken ct)
        {
            var request = tsRequest.Value;
            var context = connection.ContextAs<IronfishWorkerContext>();

            try
            {
                if (!context.IsAuthorized)
                    throw new StratumException(StratumError.UnauthorizedWorker, "not authorized");

                var (share, blockHex) = await OnSubmitShareAsync(connection, request, ct);
            }
            catch (StratumException ex)
            {
                // Client-side error
                logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{ex.Code}]");

                // Telemetry
                PublishTelemetry(TelemetryCategory.Share, ex.Code.ToString(), 1);

                // Respond
                await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id);
            }
            catch (Exception ex)
            {
                // Server-side error
                logger.Error(ex, () => $"[{connection.ConnectionId}] Error during share submission");

                // Telemetry
                PublishTelemetry(TelemetryCategory.Share, "server_error", 1);

                // Respond
                await connection.RespondErrorAsync(StratumError.Other, "server error", request.Id);
            }
        }

        private async Task OnExtraNonceSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            if (request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            await connection.RespondAsync(true, request.Id);
        }

        private async Task OnNewJobAsync(object job)
        {
            logger.Info(() => "Broadcasting job");

            await ForEachMinerAsync(async (connection, ct) =>
            {
                var context = connection.ContextAs<IronfishWorkerContext>();

                if (context.IsSubscribed && context.IsAuthorized)
                {
                    await SendJob(connection);
                }
            });
        }

        private async Task SendJob(StratumConnection connection)
        {
            var context = connection.ContextAs<IronfishWorkerContext>();
            var job = manager.GetCurrentJob();

            if (job != null)
            {
                await connection.NotifyAsync(IronfishStratumMethods.MiningNotify, job);
            }
        }

        private async Task ForEachMinerAsync(Func<StratumConnection, CancellationToken, Task> func)
        {
            var connections = ForEachConnection(ct => !ct.IsCancellationRequested);

            await Task.WhenAll(connections.Select(x => func(x, CancellationToken.None)));
        }

        public override double HashrateFromShares(double shares, double interval)
        {
            var result = shares / interval;
            return result;
        }

        public override double ShareMultiplier => 1;

        protected override async Task ConfigureAsync(ClusterConfig clusterConfig, PoolConfig poolConfig, CancellationToken ct)
        {
            await base.ConfigureAsync(clusterConfig, poolConfig, ct);

            coin = poolConfig.Template.As<IronfishCoinTemplate>();
        }

        protected override async Task SetupBansAsync(CancellationToken ct)
        {
            await base.SetupBansAsync(ct);
        }
    }

    public static class IronfishStratumMethods
    {
        public const string Subscribe = "mining.subscribe";
        public const string Authorize = "mining.authorize";
        public const string SubmitShare = "mining.submit";
        public const string SetDifficulty = "mining.set_difficulty";
        public const string MiningNotify = "mining.notify";
        public const string ExtraNonceSubscribe = "mining.extranonce.subscribe";
    }

    public class IronfishExtraNonceProvider : IExtraNonceProvider
    {
        private uint counter;

        public string Next()
        {
            var bytes = BitConverter.GetBytes(Interlocked.Increment(ref counter));
            return bytes.ToHexString();
        }

        public int ByteSize => 4;
    }
}
