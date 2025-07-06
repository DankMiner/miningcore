using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
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
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using NLog;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Ironfish
{
    public class IronfishPaymentProcessor : PaymentProcessorBase
    {
        public IronfishPaymentProcessor(
            IComponentContext ctx,
            IConnectionFactory cf,
            IMapper mapper,
            IShareRepository shareRepo,
            IBlockRepository blockRepo,
            IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
        {
            Contract.RequiresNonNull(ctx);
            Contract.RequiresNonNull(cf);
            Contract.RequiresNonNull(mapper);
            Contract.RequiresNonNull(shareRepo);
            Contract.RequiresNonNull(blockRepo);
            Contract.RequiresNonNull(balanceRepo);
            Contract.RequiresNonNull(paymentRepo);
            Contract.RequiresNonNull(clock);
            Contract.RequiresNonNull(messageBus);
        }

        protected readonly IComponentContext ctx = ctx;
        protected DaemonClient daemon;
        protected IronfishCoinTemplate coin;
        private string poolAddress;
        private const int IronfishConfirmations = 15;

        protected override string LogCategory => "Ironfish Payment Processing";

        public override async Task ConfigureAsync(ClusterConfig clusterConfig, PoolConfig poolConfig, CancellationToken ct)
        {
            Contract.RequiresNonNull(poolConfig);

            await base.ConfigureAsync(clusterConfig, poolConfig, ct);

            poolAddress = poolConfig.Address;
            coin = poolConfig.Template.As<IronfishCoinTemplate>();
        }

        public override async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
        {
            Contract.RequiresNonNull(blocks);

            var result = new List<Block>();
            var pageSize = 100;

            for (var i = 0; i < blocks.Length; i += pageSize)
            {
                var batch = blocks
                    .Skip(i)
                    .Take(Math.Min(pageSize, blocks.Length - i))
                    .ToArray();

                await Task.WhenAll(batch.Select(block => ClassifyBlockAsync(pool, block, ct)));

                result.AddRange(batch);
            }

            return result.ToArray();
        }

        private async Task ClassifyBlockAsync(IMiningPool pool, Block block, CancellationToken ct)
        {
            try
            {
                // Get block info
                var request = new JsonRpcRequest("getblock", new { hash = block.BlockHash });
                var response = await daemon.ExecuteCmdSingleAsync<IronfishBlockInfo>(logger, request, ct);

                if (response.Error != null)
                {
                    logger.Warn(() => $"Unable to fetch block {block.BlockHash}: {response.Error.Message} [{response.Error.Code}]");
                    
                    // Handle orphaned block
                    if (response.Error.Code == -5) // Block not found
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        return;
                    }

                    throw new Exception($"Unable to fetch block {block.BlockHash}: {response.Error.Message}");
                }

                var blockInfo = response.Response;

                // Check confirmations
                if (blockInfo.Confirmations < 0)
                {
                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;
                    return;
                }

                // Update block info
                block.ConfirmationProgress = Math.Min(1.0, (double)blockInfo.Confirmations / IronfishConfirmations);
                block.Reward = decimal.Parse(blockInfo.MinersFee);
                block.Status = blockInfo.Confirmations >= IronfishConfirmations ? BlockStatus.Confirmed : BlockStatus.Pending;
                block.BlockHeight = blockInfo.Sequence;
                block.NetworkDifficulty = blockInfo.Difficulty;

                if (block.Status == BlockStatus.Confirmed)
                {
                    logger.Info(() => $"Block {block.BlockHash} [{block.BlockHeight}] has been confirmed");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, () => $"Error while classifying block {block.BlockHash}");
                throw;
            }
        }

        public override async Task<decimal> UpdateBlockRewardBalancesAsync(
            IDbConnection con, IDbTransaction tx,
            IMiningPool pool, Block block, CancellationToken ct)
        {
            var blockReward = block.Reward;
            var rewardRecipients = new Dictionary<string, decimal>();

            // Pool reward
            rewardRecipients[poolAddress] = blockReward;

            // Update balances
            foreach (var recipient in rewardRecipients)
            {
                var recipientAddress = recipient.Key;
                var recipientReward = recipient.Value;

                logger.Info(() => $"Crediting {recipientReward} to {recipientAddress}");

                await balanceRepo.AddAmountAsync(con, tx, pool.Id, recipientAddress, recipientReward, $"Reward for block {block.BlockHeight}");
            }

            return blockReward;
        }

        public override async Task PayoutAsync(IMiningPool pool, Balance balance, CancellationToken ct)
        {
            Contract.RequiresNonNull(balance);

            var amount = balance.Amount;

            if (amount < poolConfig.PaymentProcessing.MinimumPayment)
            {
                logger.Info(() => $"Balance {amount} for {balance.Address} does not meet minimum payment threshold");
                return;
            }

            logger.Info(() => $"Paying {amount} to {balance.Address}");

            try
            {
                // Build transaction
                var request = new JsonRpcRequest("sendTransaction", new
                {
                    from = poolAddress,
                    to = balance.Address,
                    amount = amount.ToString(),
                    memo = $"Pool payout",
                    fee = poolConfig.PaymentProcessing.NetworkFee ?? "0.00000001"
                });

                var response = await daemon.ExecuteCmdSingleAsync<IronfishTransaction>(logger, request, ct);

                if (response.Error != null)
                {
                    throw new Exception($"Daemon returned error: {response.Error.Message} [{response.Error.Code}]");
                }

                var txHash = response.Response.Hash;

                // Persist payment
                await PersistPaymentAsync(balance, txHash, amount, ct);

                logger.Info(() => $"Payment of {amount} to {balance.Address} completed with transaction {txHash}");

                NotifyPayoutSuccess(pool.Id, balance, txHash, amount);
            }
            catch (Exception ex)
            {
                logger.Error(ex, () => $"Failed to payout {amount} to {balance.Address}");

                NotifyPayoutFailure(pool.Id, balance, ex.Message, null);
                throw;
            }
        }

        private async Task PersistPaymentAsync(Balance balance, string txHash, decimal amount, CancellationToken ct)
        {
            await cf.RunTx(async (con, tx) =>
            {
                // Reset balance
                await balanceRepo.AddAmountAsync(con, tx, balance.PoolId, balance.Address, -amount);

                // Record payment
                var payment = new Payment
                {
                    PoolId = balance.PoolId,
                    Address = balance.Address,
                    Amount = amount,
                    TransactionConfirmationData = txHash,
                    Created = clock.Now
                };

                await paymentRepo.InsertAsync(con, tx, payment);
            });
        }
    }

    public class IronfishBlockInfo
    {
        public long Sequence { get; set; }
        public string Hash { get; set; }
        public string PreviousBlockHash { get; set; }
        public long Timestamp { get; set; }
        public int Confirmations { get; set; }
        public double Difficulty { get; set; }
        public string MinersFee { get; set; }
        public int Size { get; set; }
        public List<string> Transactions { get; set; }
    }
}
