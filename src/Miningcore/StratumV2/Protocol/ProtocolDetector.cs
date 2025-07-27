using System;
using System.IO;
using System.Threading.Tasks;

namespace Miningcore.StratumV2.Protocol
{
    public enum StratumProtocolVersion
    {
        Unknown,
        StratumV1,
        StratumV2
    }

    public class ProtocolDetectionResult
    {
        public StratumProtocolVersion Protocol { get; set; }
        public byte[] InitialData { get; set; }
        public bool IsComplete { get; set; }
    }

    public class ProtocolDetector
    {
        private const int DETECTION_BUFFER_SIZE = 32; // Bytes needed to determine protocol
        
        public static ProtocolDetectionResult DetectProtocol(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return new ProtocolDetectionResult 
                { 
                    Protocol = StratumProtocolVersion.Unknown, 
                    InitialData = data,
                    IsComplete = false 
                };
            }

            // Check for SV1 (JSON-RPC)
            if (IsStratumV1(data))
            {
                return new ProtocolDetectionResult
                {
                    Protocol = StratumProtocolVersion.StratumV1,
                    InitialData = data,
                    IsComplete = true
                };
            }

            // Check for SV2 (Binary)
            if (IsStratumV2(data))
            {
                return new ProtocolDetectionResult
                {
                    Protocol = StratumProtocolVersion.StratumV2,
                    InitialData = data,
                    IsComplete = true
                };
            }

            // Need more data for detection
            return new ProtocolDetectionResult
            {
                Protocol = StratumProtocolVersion.Unknown,
                InitialData = data,
                IsComplete = data.Length >= DETECTION_BUFFER_SIZE
            };
        }

        private static bool IsStratumV1(byte[] data)
        {
            // SV1 characteristics:
            // 1. Starts with '{' (JSON)
            // 2. Contains common JSON-RPC fields
            // 3. Printable ASCII text
            
            if (data[0] == 0x7B) // '{'
            {
                try
                {
                    var text = System.Text.Encoding.UTF8.GetString(data);
                    
                    // Look for common SV1 patterns
                    return text.Contains("\"method\"") || 
                           text.Contains("\"id\"") || 
                           text.Contains("mining.subscribe") ||
                           text.Contains("mining.authorize");
                }
                catch
                {
                    // Not valid UTF-8, probably not SV1
                    return false;
                }
            }

            return false;
        }

        private static bool IsStratumV2(byte[] data)
        {
            // SV2 message format:
            // [extension_type: 2 bytes][msg_type: 1 byte][length: 4 bytes][payload: length bytes]
            
            if (data.Length < 6) // Minimum SV2 message size
                return false;

            try
            {
                var extensionType = BitConverter.ToUInt16(data, 0);
                var messageType = data[2];
                var length = BitConverter.ToUInt32(data, 3);

                // Validate SV2 message structure
                return IsValidSV2MessageType(messageType) && 
                       IsValidSV2Length(length, data.Length) &&
                       IsValidSV2ExtensionType(extensionType);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidSV2MessageType(byte messageType)
        {
            // Check if message type is in valid SV2 range
            return messageType switch
            {
                // Common Protocol Messages (0x00-0x0F)
                >= 0x00 and <= 0x0F => true,
                
                // Mining Protocol Messages (0x10-0x1F)
                >= 0x10 and <= 0x1F => true,
                
                // Job Declaration Protocol Messages (0x50-0x5F)
                >= 0x50 and <= 0x5F => true,
                
                // Template Distribution Protocol Messages (0x70-0x7F)
                >= 0x70 and <= 0x7F => true,
                
                _ => false
            };
        }

        private static bool IsValidSV2Length(uint length, int dataLength)
        {
            // Basic sanity checks for message length
            return length <= 0x1000000 && // Max 16MB (reasonable limit)
                   length + 7 <= dataLength + 1000; // Allow for partial messages
        }

        private static bool IsValidSV2ExtensionType(ushort extensionType)
        {
            // Most SV2 messages use extension_type = 0
            // Custom extensions might use other values
            return extensionType <= 0x1000; // Reasonable upper bound
        }
    }
}
