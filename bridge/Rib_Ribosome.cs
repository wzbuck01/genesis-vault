using System.Diagnostics;
namespace Genesis.Governance;

public sealed record RibosomeDecision(
    GovernanceVerdict Verdict,
    RibosomeReceipt Receipt);

public sealed class Ribosome
{
    private readonly RibosomeThresholds thresholds;

    public Ribosome(RibosomeThresholds thresholds)
    {
        this.thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));
        thresholds.Validate();
    }

    public GovernanceVerdict Decide(RibosomeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.ReceiptSatisfied) return GovernanceVerdict.RequireReceipt;
        if (!state.AuthoritySatisfied) return GovernanceVerdict.RequireAuthority;
        if (!float.IsFinite(state.GovernanceScore))
            throw new ArgumentOutOfRangeException(nameof(state));

        if (state.GovernanceScore < thresholds.RejectBelow)
            return GovernanceVerdict.Reject;
        if (state.GovernanceScore < thresholds.AcceptAtOrAbove)
            return GovernanceVerdict.Repair;
        return GovernanceVerdict.Accept;
    }

    public RibosomeDecision Evaluate(
        GovernanceAdapterReceipt phase3,
        RibosomeState state)
    {
        ArgumentNullException.ThrowIfNull(phase3);
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.EvidenceHash))
            throw new ArgumentException("EvidenceHash is required.", nameof(state));

        var started = Stopwatch.GetTimestamp();
        var verdict = Decide(state);
        var latency = checked(phase3.LatencyNs +
            StopwatchTicks.ToNanoseconds(Stopwatch.GetTimestamp() - started));

        var receipt = new RibosomeReceipt(
            phase3.InputIdentity, phase3.PolicyIdentity, phase3.PreWitness,
            phase3.PostWitness, latency, phase3.OutputIdentity, phase3.ClassType,
            verdict, state.EvidenceHash);

        return new RibosomeDecision(verdict, receipt);
    }
}
