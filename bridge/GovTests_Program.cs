using Genesis.Governance;

var r = new GovernanceAdapterReceipt("in","policy","pre","post",100,"out",7);
var ribosome = new Ribosome(new RibosomeThresholds(0.25f, 0.75f));

var tests = new (string, Action)[]
{
    ("missing receipt defers", () => Eq(ribosome.Decide(S(1, false, true)), GovernanceVerdict.RequireReceipt)),
    ("missing authority defers", () => Eq(ribosome.Decide(S(1, true, false)), GovernanceVerdict.RequireAuthority)),
    ("receipt precedence", () => Eq(ribosome.Decide(S(1, false, false)), GovernanceVerdict.RequireReceipt)),
    ("reject band", () => Eq(ribosome.Decide(S(0.249f)), GovernanceVerdict.Reject)),
    ("repair lower boundary", () => Eq(ribosome.Decide(S(0.25f)), GovernanceVerdict.Repair)),
    ("repair upper band", () => Eq(ribosome.Decide(S(0.749f)), GovernanceVerdict.Repair)),
    ("accept boundary", () => Eq(ribosome.Decide(S(0.75f)), GovernanceVerdict.Accept)),
    ("receipt compatibility", () => {
        var x = ribosome.Evaluate(r, S(1, evidence:"sha256:abc")).Receipt;
        Eq(x.InputIdentity, r.InputIdentity); Eq(x.PolicyIdentity, r.PolicyIdentity);
        Eq(x.PreWitness, r.PreWitness); Eq(x.PostWitness, r.PostWitness);
        Eq(x.OutputIdentity, r.OutputIdentity); Eq(x.ClassType, r.ClassType);
        if (x.LatencyNs < r.LatencyNs) throw new Exception("latency regressed");
    }),
    ("evidence preserved", () => Eq(ribosome.Evaluate(r, S(1, evidence:"sha256:abc")).Receipt.EvidenceHash, "sha256:abc")),
    ("invalid thresholds fail", () => MustThrow(() => new Ribosome(new(0.8f,0.7f)))),
    ("nonfinite score fails", () => MustThrow(() => ribosome.Decide(S(float.NaN))))
};

var failed = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL  {name}: {e.Message}"); }
}
Console.WriteLine($"{tests.Length-failed}/{tests.Length} PASS");
return failed == 0 ? 0 : 1;

static RibosomeState S(float score, bool receipt=true, bool authority=true, string evidence="evidence")
    => new(score, receipt, authority, evidence);
static void Eq<T>(T a, T b) where T:notnull
{
    if (!EqualityComparer<T>.Default.Equals(a,b)) throw new Exception($"{a} != {b}");
}
static void MustThrow(Action a)
{
    try { a(); } catch { return; }
    throw new Exception("expected exception");
}
