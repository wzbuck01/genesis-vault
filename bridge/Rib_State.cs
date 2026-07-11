namespace Genesis.Governance;
public sealed record RibosomeState(
    float GovernanceScore,
    bool ReceiptSatisfied,
    bool AuthoritySatisfied,
    string EvidenceHash);
