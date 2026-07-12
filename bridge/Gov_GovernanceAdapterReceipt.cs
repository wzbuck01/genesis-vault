namespace Genesis.Governance;
public record GovernanceAdapterReceipt(
    string InputIdentity, string PolicyIdentity, string PreWitness,
    string PostWitness, long LatencyNs, string OutputIdentity, int ClassType);
