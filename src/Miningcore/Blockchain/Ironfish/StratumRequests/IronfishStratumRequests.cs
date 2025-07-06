using Newtonsoft.Json;

namespace Miningcore.Blockchain.Ironfish.StratumRequests
{
    public class IronfishSubscribeRequest
    {
        /// <summary>
        /// User agent/version
        /// </summary>
        [JsonProperty(Order = 0)]
        public string UserAgent { get; set; }

        /// <summary>
        /// Session ID (optional)
        /// </summary>
        [JsonProperty(Order = 1, NullValueHandling = NullValueHandling.Ignore)]
        public string SessionId { get; set; }
    }

    public class IronfishAuthorizeRequest
    {
        /// <summary>
        /// Worker name in the format address.workername
        /// </summary>
        [JsonProperty(Order = 0)]
        public string Worker { get; set; }

        /// <summary>
        /// Worker password (optional)
        /// </summary>
        [JsonProperty(Order = 1)]
        public string Password { get; set; }
    }

    public class IronfishSubmitShareRequest
    {
        /// <summary>
        /// Worker name
        /// </summary>
        [JsonProperty(Order = 0)]
        public string WorkerName { get; set; }

        /// <summary>
        /// Job ID
        /// </summary>
        [JsonProperty(Order = 1)]
        public string JobId { get; set; }

        /// <summary>
        /// ExtraNonce2
        /// </summary>
        [JsonProperty(Order = 2)]
        public string ExtraNonce2 { get; set; }

        /// <summary>
        /// nTime
        /// </summary>
        [JsonProperty(Order = 3)]
        public string NTime { get; set; }

        /// <summary>
        /// Nonce
        /// </summary>
        [JsonProperty(Order = 4)]
        public string Nonce { get; set; }
    }

    public class IronfishExtraNonceSubscribeRequest
    {
        // No parameters
    }
}
