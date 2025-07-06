using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Miningcore.Extensions;

namespace Miningcore.Blockchain.Ironfish
{
    public class Target
    {
        private readonly BigInteger targetValue;
        
        public Target(string targetHex)
        {
            if (string.IsNullOrEmpty(targetHex))
                throw new ArgumentException("Target cannot be null or empty", nameof(targetHex));
                
            targetValue = BigInteger.Parse(targetHex, NumberStyles.HexNumber);
        }
        
        public Target(BigInteger target)
        {
            targetValue = target;
        }
        
        public double Difficulty
        {
            get
            {
                // Ironfish difficulty calculation
                // diff = max_target / current_target
                var maxTarget = IronfishConstants.Diff1Target;
                var difficulty = (double)(maxTarget / targetValue);
                return Math.Max(difficulty, 0.0001);
            }
        }
        
        public BigInteger Value => targetValue;
        
        public string ToHexString()
        {
            return targetValue.ToString("x64");
        }
        
        public static Target FromDifficulty(double difficulty)
        {
            if (difficulty <= 0)
                throw new ArgumentException("Difficulty must be greater than 0", nameof(difficulty));
                
            var maxTarget = IronfishConstants.Diff1Target;
            var target = maxTarget / new BigInteger(difficulty);
            return new Target(target);
        }
        
        public static bool operator <(Target left, Target right)
        {
            return left.targetValue < right.targetValue;
        }
        
        public static bool operator >(Target left, Target right)
        {
            return left.targetValue > right.targetValue;
        }
        
        public static bool operator <=(Target left, Target right)
        {
            return left.targetValue <= right.targetValue;
        }
        
        public static bool operator >=(Target left, Target right)
        {
            return left.targetValue >= right.targetValue;
        }
        
        public static bool operator ==(Target left, Target right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (ReferenceEquals(left, null)) return false;
            if (ReferenceEquals(right, null)) return false;
            return left.targetValue == right.targetValue;
        }
        
        public static bool operator !=(Target left, Target right)
        {
            return !(left == right);
        }
        
        public override bool Equals(object obj)
        {
            if (obj is Target other)
                return targetValue == other.targetValue;
            return false;
        }
        
        public override int GetHashCode()
        {
            return targetValue.GetHashCode();
        }
    }
    
    public static class IronfishDifficulty
    {
        /// <summary>
        /// Converts a share difficulty to stratum difficulty
        /// </summary>
        public static double ShareDifficultyToStratumDifficulty(double shareDifficulty)
        {
            return shareDifficulty;
        }
        
        /// <summary>
        /// Converts stratum difficulty to share difficulty
        /// </summary>
        public static double StratumDifficultyToShareDifficulty(double stratumDifficulty)
        {
            return stratumDifficulty;
        }
        
        /// <summary>
        /// Calculates the difficulty from a hash
        /// </summary>
        public static double DifficultyFromHash(byte[] hash)
        {
            var hashBigInt = new BigInteger(hash.Reverse().Concat(new byte[] { 0 }).ToArray());
            if (hashBigInt == 0)
                return double.MaxValue;
                
            var difficulty = (double)(IronfishConstants.Diff1Target / hashBigInt);
            return Math.Max(difficulty, 0.0001);
        }
        
        /// <summary>
        /// Checks if a hash meets the target difficulty
        /// </summary>
        public static bool CheckHash(byte[] hash, double targetDifficulty)
        {
            var hashDifficulty = DifficultyFromHash(hash);
            return hashDifficulty >= targetDifficulty;
        }
        
        /// <summary>
        /// Converts difficulty to target hex string
        /// </summary>
        public static string DifficultyToTargetHex(double difficulty)
        {
            var target = Target.FromDifficulty(difficulty);
            return target.ToHexString();
        }
    }
}
