namespace Genesis.Governance;

public sealed record GovernanceAdapterResult<TTensor>(
    TTensor ProjectedQ,
    TTensor ProjectedK,
    GovernanceAdapterReceipt Receipt);
