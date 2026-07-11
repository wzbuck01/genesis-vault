namespace Genesis.Governance;
public sealed record RibosomeReceipt(
    string InputIdentity, string PolicyIdentity, string PreWitness,
    string PostWitness, long LatencyNs, string OutputIdentity, int ClassType,
    GovernanceVerdict Verdict, string EvidenceHash)
    : GovernanceAdapterReceipt(InputIdentity, PolicyIdentity, PreWitness,
      PostWitness, LatencyNs, OutputIdentity, ClassType);
