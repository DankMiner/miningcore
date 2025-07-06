using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Blake3Core;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Ironfish
{
    public class IronfishJob
    {
        public IronfishJob(string id, IronfishBlockTemplate blockTemplate, string poolAddress)
        {
            JobId = id;
            BlockTemplate = blockTemplate;
            PoolAddress = poolAddress;
            
            var target = new Target(BlockTemplate.Target);
            Difficulty = target.Difficulty;
            
            jobParams = new object[]
            {
                JobId,
                BlockTemplate.Header.Sequence.ToString(),
                BlockTemplate.Header.PreviousBlockHash,
                BlockTemplate.Header.NoteCommitment,
                BlockTemplate.Target,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockTemplate.Header.RandomnessBeacon,
                BlockTemplate.Header.GraffitiField
            };
        }

        public string JobId { get; }
        public IronfishBlockTemplate BlockTemplate { get; }
        public double Difficulty { get; }
        public string PoolAddress { get; }
        
        private readonly object[] jobParams;
        
        private bool RegisteredShare(string minerAddress, string workerName, double difficulty)
        {
            var key = $"{minerAddress}:{workerName}:{difficulty}";
            return submittedShares.Contains(key);
        }
        
        private void RegisterShare(string minerAddress, string workerName, double difficulty)
        {
            var key = $"{minerAddress}:{workerName}:{difficulty}";
            submittedShares.Add(key);
        }

        private readonly HashSet<string> submittedShares = new HashSet<string>();

        public (Share share, string blockHex, string blockHash) ProcessShare(
            StratumConnection worker,
            string workerName,
            string extraNonce1,
            string extraNonce2,
            string nTime,
            string nonce)
        {
            Contract.RequiresNonNull(worker);
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce1));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce2));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nTime));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

            var context = worker.ContextAs<IronfishWorkerContext>();
            
            // Validate nonce
            if (nonce.Length != 16)
                throw new StratumException(StratumError.Other, "incorrect size of nonce");

            // Validate nTime
            var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);
            var nTimestamp = DateTimeOffset.FromUnixTimeSeconds(nTimeInt).UtcDateTime;
            var now = DateTime.UtcNow;
            
            if (nTimestamp < now.AddMinutes(-10) || nTimestamp > now.AddMinutes(10))
                throw new StratumException(StratumError.Other, "ntime out of range");

            // Build block header
            var headerBytes = SerializeHeader(extraNonce1, extraNonce2, nTime, nonce);
            var headerHash = ComputeBlockHash(headerBytes);
            var headerHashHex = headerHash.ToHexString();
            
            // Calculate share difficulty
            var shareDiff = ComputeShareDifficulty(headerHash);
            
            // Check if the share meets the pool target
            var stratumDifficulty = context.Difficulty;
            var ratio = shareDiff / stratumDifficulty;
            var isBlockCandidate = shareDiff >= BlockTemplate.Difficulty;

            if (!isBlockCandidate && ratio < 0.99)
            {
                if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                {
                    ratio = shareDiff / context.PreviousDifficulty.Value;
                    
                    if (ratio < 0.99)
                        throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
                    
                    stratumDifficulty = context.PreviousDifficulty.Value;
                }
                else
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
            }

            // Check duplicate shares
            if (RegisteredShare(context.MinerName, workerName, stratumDifficulty))
                throw new StratumException(StratumError.DuplicateShare, "duplicate share");
            
            RegisterShare(context.MinerName, workerName, stratumDifficulty);

            var result = new Share
            {
                BlockHeight = BlockTemplate.Height,
                NetworkDifficulty = BlockTemplate.Difficulty,
                Difficulty = stratumDifficulty,
            };

            string blockHex = null;
            string blockHashHex = null;

            if (isBlockCandidate)
            {
                result.IsBlockCandidate = true;
                blockHex = SerializeBlock(headerBytes, extraNonce1, extraNonce2, nTime, nonce);
                blockHashHex = headerHashHex;
            }

            return (result, blockHex, blockHashHex);
        }

        public object GetJobParams()
        {
            return jobParams;
        }

        private byte[] SerializeHeader(string extraNonce1, string extraNonce2, string nTime, string nonce)
        {
            // Ironfish block header serialization
            var header = new List<byte>();
            
            // Sequence (8 bytes)
            header.AddRange(BitConverter.GetBytes(BlockTemplate.Header.Sequence));
            
            // Previous block hash (32 bytes)
            header.AddRange(BlockTemplate.Header.PreviousBlockHash.HexToByteArray());
            
            // Note commitment (32 bytes)
            header.AddRange(BlockTemplate.Header.NoteCommitment.HexToByteArray());
            
            // Nullifier commitment (32 bytes)
            header.AddRange(BlockTemplate.Header.NullifierCommitment.HexToByteArray());
            
            // Target (32 bytes)
            header.AddRange(BlockTemplate.Target.HexToByteArray());
            
            // Randomness beacon (8 bytes)
            header.AddRange(BlockTemplate.Header.RandomnessBeacon.HexToByteArray());
            
            // Timestamp (8 bytes)
            var timestamp = uint.Parse(nTime, NumberStyles.HexNumber);
            header.AddRange(BitConverter.GetBytes(timestamp));
            
            // Graffiti (32 bytes)
            header.AddRange(BlockTemplate.Header.GraffitiField.HexToByteArray());
            
            // Nonce (8 bytes)
            header.AddRange(nonce.HexToByteArray());
            
            return header.ToArray();
        }

        private string SerializeBlock(byte[] headerBytes, string extraNonce1, string extraNonce2, string nTime, string nonce)
        {
            // Complete block serialization including transactions
            var block = new List<byte>();
            
            // Add header
            block.AddRange(headerBytes);
            
            // Add transaction count
            var txCount = BlockTemplate.Transactions.Count;
            block.AddRange(VarInt.GetBytes((ulong)txCount));
            
            // Add transactions
            foreach (var tx in BlockTemplate.Transactions)
            {
                block.AddRange(tx.Serialize());
            }
            
            return block.ToArray().ToHexString();
        }

        private byte[] ComputeBlockHash(byte[] headerBytes)
        {
            // Ironfish uses Blake3 for block hashing
            return Blake3.ComputeHash(headerBytes);
        }

        private double ComputeShareDifficulty(byte[] hash)
        {
            var hashBigInt = new BigInteger(hash.Reverse().ToArray());
            var target = IronfishConstants.Diff1Target / hashBigInt;
            return Math.Max(target.ToDouble(), 0);
        }
    }

    public class IronfishBlockTemplate
    {
        public long Height { get; set; }
        public IronfishBlockHeader Header { get; set; }
        public string Target { get; set; }
        public double Difficulty { get; set; }
        public List<IronfishTransaction> Transactions { get; set; }
        public string MinersFee { get; set; }
    }

    public class IronfishBlockHeader
    {
        public long Sequence { get; set; }
        public string PreviousBlockHash { get; set; }
        public string NoteCommitment { get; set; }
        public string NullifierCommitment { get; set; }
        public string RandomnessBeacon { get; set; }
        public string GraffitiField { get; set; }
        public long Timestamp { get; set; }
    }

    public class IronfishTransaction
    {
        public string Hash { get; set; }
        public string Data { get; set; }
        
        public byte[] Serialize()
        {
            return Data.HexToByteArray();
        }
    }

    public static class IronfishConstants
    {
        public static readonly BigInteger Diff1Target = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
        public const int ExtraNonceSize = 8;
        public const int NonceSize = 8;
    }

    public static class Blake3
    {
        public static byte[] ComputeHash(byte[] data)
        {
            // Using the Blake3.NET library
            var hasher = new Blake3Core.Hasher();
            hasher.Update(data);
            var hash = hasher.Finalize();
            return hash.AsSpan().ToArray();
        }
    }

    public static class VarInt
    {
        public static byte[] GetBytes(ulong value)
        {
            if (value < 0xfd)
                return new[] { (byte)value };
            else if (value <= 0xffff)
                return BitConverter.GetBytes((ushort)value).Prepend((byte)0xfd).ToArray();
            else if (value <= 0xffffffff)
                return BitConverter.GetBytes((uint)value).Prepend((byte)0xfe).ToArray();
            else
                return BitConverter.GetBytes(value).Prepend((byte)0xff).ToArray();
        }
    }
}
