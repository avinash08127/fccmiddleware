using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualLab.Domain.Benchmarking;

public sealed record BenchmarkSeedProfile
{
    [JsonPropertyName("profileName")]
    public string ProfileName { get; init; } = "phase0-default";

    [JsonPropertyName("sites")]
    public int Sites { get; init; }

    [JsonPropertyName("pumpsPerSite")]
    public int PumpsPerSite { get; init; }

    [JsonPropertyName("nozzlesPerPump")]
    public int NozzlesPerPump { get; init; }

    [JsonPropertyName("transactions")]
    public int Transactions { get; init; }

    [JsonPropertyName("pushTransactions")]
    public int PushTransactions { get; init; }

    [JsonPropertyName("pullTransactions")]
    public int PullTransactions { get; init; }

    [JsonPropertyName("hybridTransactions")]
    public int HybridTransactions { get; init; }

    [JsonPropertyName("normalOrderTransactions")]
    public int NormalOrderTransactions { get; init; }

    [JsonPropertyName("preAuthCreateOnlyTransactions")]
    public int PreAuthCreateOnlyTransactions { get; init; }

    [JsonPropertyName("preAuthCreateThenAuthorizeTransactions")]
    public int PreAuthCreateThenAuthorizeTransactions { get; init; }

    [JsonPropertyName("callbackTargets")]
    public int CallbackTargets { get; init; }

    [JsonPropertyName("scenarioSeed")]
    public int ScenarioSeed { get; init; }

    [JsonPropertyName("replayIterations")]
    public int ReplayIterations { get; init; } = 3;

    public int TotalPumps => Sites * PumpsPerSite;

    public int TotalNozzles => TotalPumps * NozzlesPerPump;

    public string ComputeReplaySignature()
    {
        string json = JsonSerializer.Serialize(this);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }
}
