using System;
using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms
{
    public unsafe class ProgPowQuai : IHashAlgorithm
    {
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        {
            Contract.Requires<ArgumentException>(result.Length >= 32);
            Contract.Requires<ArgumentException>(extra.Length >= 2);
            
            var height = (ulong) extra[0];
            var nonce = (ulong) extra[1];
            
            fixed (byte* input = data)
            fixed (byte* output = result)
            {
                // Call native ProgPoW implementation
                // This would need to be implemented in native code
                LibMultihash.progpow_quai(input, (uint) data.Length, height, nonce, output);
            }
        }
    }
}
