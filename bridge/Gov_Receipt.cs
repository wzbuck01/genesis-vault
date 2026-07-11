namespace Genesis.Governance;

public sealed record GovernanceAdapterReceipt(
    string InputIdentity,
    string PolicyIdentity,
    string PreWitness,
    string PostWitness,
    long LatencyNs,
    string OutputIdentity,
    int ClassType);
