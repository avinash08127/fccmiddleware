using System.Security.Cryptography;
using System.Text;

namespace VirtualLab.Infrastructure.Scenarios;

public sealed class ScenarioExecutionScope
{
    public ScenarioRuntimeContext? Current { get; private set; }

    public IDisposable Begin(Guid runId, string scenarioKey, string siteCode, int replaySeed)
    {
        if (Current is not null)
        {
            throw new InvalidOperationException("A scenario execution context is already active for this scope.");
        }

        ScenarioRuntimeContext context = new(runId, scenarioKey, siteCode, replaySeed);
        Current = context;
        return new ScopeHandle(this, context);
    }

    private void End(ScenarioRuntimeContext context)
    {
        if (ReferenceEquals(Current, context))
        {
            Current = null;
        }
    }

    private sealed class ScopeHandle(ScenarioExecutionScope owner, ScenarioRuntimeContext context) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner.End(context);
            _disposed = true;
        }
    }
}

public sealed class ScenarioRuntimeContext
{
    private readonly Dictionary<string, int> _preAuthSequences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _transactionSequences = new(StringComparer.OrdinalIgnoreCase);

    public ScenarioRuntimeContext(Guid runId, string scenarioKey, string siteCode, int replaySeed)
    {
        RunId = runId;
        ScenarioKey = scenarioKey;
        SiteCode = siteCode;
        ReplaySeed = replaySeed;
        ScenarioToken = ComputeToken(scenarioKey);
    }

    public Guid RunId { get; }

    public string ScenarioKey { get; }

    public string SiteCode { get; }

    public int ReplaySeed { get; }

    public string ScenarioToken { get; }

    public int NextTransactionSequence(int pumpNumber, int nozzleNumber, string? correlationId)
    {
        string key = BuildSequenceKey("tx", pumpNumber, nozzleNumber, correlationId);
        return NextSequence(_transactionSequences, key);
    }

    public int NextPreAuthSequence(int pumpNumber, int nozzleNumber, string? correlationId)
    {
        string key = BuildSequenceKey("pa", pumpNumber, nozzleNumber, correlationId);
        return NextSequence(_preAuthSequences, key);
    }

    public string CreateTransactionExternalId(int pumpNumber, int nozzleNumber, int sequence)
        => $"TX-{ReplaySeed:D6}-{ScenarioToken}-{pumpNumber:D2}{nozzleNumber:D2}-{sequence:D4}";

    public string CreatePreAuthExternalId(int pumpNumber, int nozzleNumber, int sequence)
        => $"PA-{ReplaySeed:D6}-{ScenarioToken}-{pumpNumber:D2}{nozzleNumber:D2}-{sequence:D4}";

    public Guid CreateDeterministicGuid(string purpose)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ScenarioKey}|{SiteCode}|{ReplaySeed}|{purpose}"));
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static int NextSequence(IDictionary<string, int> sequences, string key)
    {
        int next = sequences.TryGetValue(key, out int current) ? current + 1 : 1;
        sequences[key] = next;
        return next;
    }

    private string BuildSequenceKey(string prefix, int pumpNumber, int nozzleNumber, string? correlationId)
        => $"{prefix}|{ScenarioKey}|{SiteCode}|{pumpNumber:D2}|{nozzleNumber:D2}|{correlationId ?? string.Empty}";

    private static string ComputeToken(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash[..2]);
    }
}
