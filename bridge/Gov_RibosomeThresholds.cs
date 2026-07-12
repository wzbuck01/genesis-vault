namespace Genesis.Governance;
public sealed record RibosomeThresholds(float RejectBelow, float AcceptAtOrAbove)
{
    public void Validate()
    {
        if (!float.IsFinite(RejectBelow) || !float.IsFinite(AcceptAtOrAbove))
            throw new ArgumentOutOfRangeException(nameof(RejectBelow));
        if (RejectBelow > AcceptAtOrAbove)
            throw new ArgumentException("RejectBelow must be <= AcceptAtOrAbove.");
    }
}
