using Newtonsoft.Json;
using System.Collections.Generic;

namespace Miningcore.Blockchain.Ironfish.DaemonResponses
{
    public class IronfishGetBlockTemplateResponse
    {
        [JsonProperty("header")]
        public IronfishBlockHeaderResponse Header { get; set; }

        [JsonProperty("transactions")]
        public List<IronfishTransactionResponse> Transactions { get; set; }

        [JsonProperty("target")]
        public string Target { get; set; }

        [JsonProperty("difficulty")]
        public double Difficulty { get; set; }

        [JsonProperty("height")]
        public long Height { get; set; }

        [JsonProperty("minersFee")]
        public string MinersFee { get; set; }
    }

    public class IronfishBlockHeaderResponse
    {
        [JsonProperty("sequence")]
        public long Sequence { get; set; }

        [JsonProperty("previousBlockHash")]
        public string PreviousBlockHash { get; set; }

        [JsonProperty("noteCommitment")]
        public string NoteCommitment { get; set; }

        [JsonProperty("nullifierCommitment")]
        public string NullifierCommitment { get; set; }

        [JsonProperty("randomnessBeacon")]
        public string RandomnessBeacon { get; set; }

        [JsonProperty("graffitiField")]
        public string GraffitiField { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }
    }

    public class IronfishTransactionResponse
    {
        [JsonProperty("hash")]
        public string Hash { get; set; }

        [JsonProperty("serialized")]
        public string Serialized { get; set; }

        [JsonProperty("fee")]
        public string Fee { get; set; }

        [JsonProperty("spends")]
        public List<IronfishSpendResponse> Spends { get; set; }

        [JsonProperty("outputs")]
        public List<IronfishOutputResponse> Outputs { get; set; }
    }

    public class IronfishSpendResponse
    {
        [JsonProperty("nullifier")]
        public string Nullifier { get; set; }

        [JsonProperty("commitment")]
        public string Commitment { get; set; }

        [JsonProperty("size")]
        public int Size { get; set; }
    }

    public class IronfishOutputResponse
    {
        [JsonProperty("commitment")]
        public string Commitment { get; set; }

        [JsonProperty("size")]
        public int Size { get; set; }
    }

    public class IronfishSubmitBlockResponse
    {
        [JsonProperty("hash")]
        public string Hash { get; set; }

        [JsonProperty("accepted")]
        public bool Accepted { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    public class IronfishGetBlockResponse
    {
        [JsonProperty("sequence")]
        public long Sequence { get; set; }

        [JsonProperty("hash")]
        public string Hash { get; set; }

        [JsonProperty("previousBlockHash")]
        public string PreviousBlockHash { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

        [JsonProperty("confirmations")]
        public int Confirmations { get; set; }

        [JsonProperty("difficulty")]
        public double Difficulty { get; set; }

        [JsonProperty("minersFee")]
        public string MinersFee { get; set; }

        [JsonProperty("size")]
        public int Size { get; set; }

        [JsonProperty("transactions")]
        public List<string> Transactions { get; set; }
    }

    public class IronfishGetBlockchainInfoResponse
    {
        [JsonProperty("blocks")]
        public int Blocks { get; set; }

        [JsonProperty("headers")]
        public int Headers { get; set; }

        [JsonProperty("synced")]
        public bool Synced { get; set; }

        [JsonProperty("head")]
        public IronfishBlockInfoResponse Head { get; set; }

        [JsonProperty("networkHash")]
        public string NetworkHash { get; set; }

        [JsonProperty("networkDifficulty")]
        public string NetworkDifficulty { get; set; }
    }

    public class IronfishBlockInfoResponse
    {
        [JsonProperty("hash")]
        public string Hash { get; set; }

        [JsonProperty("sequence")]
        public long Sequence { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }
    }

    public class IronfishGetNetworkInfoResponse
    {
        [JsonProperty("networkId")]
        public int NetworkId { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("connections")]
        public int Connections { get; set; }
    }

    public class IronfishGetPeerInfoResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("addr")]
        public string Addr { get; set; }

        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("bytesReceived")]
        public long BytesReceived { get; set; }

        [JsonProperty("bytesSent")]
        public long BytesSent { get; set; }
    }

    public class IronfishGetAccountsResponse
    {
        [JsonProperty("accounts")]
        public List<IronfishAccountResponse> Accounts { get; set; }
    }

    public class IronfishAccountResponse
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("address")]
        public string Address { get; set; }

        [JsonProperty("viewKey")]
        public string ViewKey { get; set; }

        [JsonProperty("incomingViewKey")]
        public string IncomingViewKey { get; set; }

        [JsonProperty("balance")]
        public IronfishBalanceResponse Balance { get; set; }
    }

    public class IronfishBalanceResponse
    {
        [JsonProperty("confirmed")]
        public string Confirmed { get; set; }

        [JsonProperty("unconfirmed")]
        public string Unconfirmed { get; set; }

        [JsonProperty("pending")]
        public string Pending { get; set; }
    }

    public class IronfishSendTransactionResponse
    {
        [JsonProperty("hash")]
        public string Hash { get; set; }

        [JsonProperty("accepted")]
        public bool Accepted { get; set; }
    }

    public class IronfishEstimateFeeRatesResponse
    {
        [JsonProperty("slow")]
        public string Slow { get; set; }

        [JsonProperty("average")]
        public string Average { get; set; }

        [JsonProperty("fast")]
        public string Fast { get; set; }
    }
}
