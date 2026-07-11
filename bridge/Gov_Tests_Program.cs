using Genesis.Governance;

var tests = new (string Name, Action Run)[]
{
    ("canonical serialization deterministic", CanonicalSerializationIsDeterministic),
    ("receipt deterministic except latency", ReceiptIsDeterministic),
    ("projection matches direct application", ProjectionMatchesDirectApplication),
    ("effectful projection changes witness", EffectfulProjectionChangesWitness),
    ("identity projection preserves witness", IdentityProjectionPreservesWitness),
    ("policy identity changes with policy", PolicyIdentityChangesWithPolicy),
    ("input remains immutable", InputRemainsImmutable),
    ("nullable class uses default", NullableClassUsesDefault),
    ("latency is nonnegative", LatencyIsNonnegative),
};

var failures = 0;

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} PASS");
return failures == 0 ? 0 : 1;

static GovernanceAdapter<Pole, float[]> CreateAdapter(
    float[,]? matrix = null,
    Func<Pole, int?>? classType = null)
{
    matrix ??= new float[,]
    {
        { 1f, 1f, 1f, 1f },
        { 1f, 0.5f, 0f, -1f },
    };

    return new GovernanceAdapter<Pole, float[]>(
        classType ?? (pole => pole.ClassType()),
        () => matrix,
        policy => policy.ToArray(),
        ApplyGates,
        defaultClassType: 0);
}

static float[] ApplyGates(float[] tensor, ReadOnlyMemory<float> gates)
{
    var result = new float[tensor.Length];
    var gateSpan = gates.Span;

    for (var i = 0; i < tensor.Length; i++)
        result[i] = tensor[i] * gateSpan[i % gateSpan.Length];

    return result;
}

static void CanonicalSerializationIsDeterministic()
{
    var tensor = new[] { 1f, -0f, float.NaN, 4.5f };
    AssertEqual(
        TensorCanonicalizer.Serialize(tensor),
        TensorCanonicalizer.Serialize(tensor));
}

static void ReceiptIsDeterministic()
{
    var adapter = CreateAdapter();
    var q = new[] { 1f, 2f, 3f, 4f };
    var k = new[] { 4f, 3f, 2f, 1f };

    var a = adapter.Apply(q, k, new Pole(1)).Receipt;
    var b = adapter.Apply(q, k, new Pole(1)).Receipt;

    Assert(a.InputIdentity == b.InputIdentity, "input identity differs");
    Assert(a.PolicyIdentity == b.PolicyIdentity, "policy identity differs");
    Assert(a.PreWitness == b.PreWitness, "pre witness differs");
    Assert(a.PostWitness == b.PostWitness, "post witness differs");
    Assert(a.OutputIdentity == b.OutputIdentity, "output identity differs");
    Assert(a.ClassType == b.ClassType, "class type differs");
}

static void ProjectionMatchesDirectApplication()
{
    var adapter = CreateAdapter();
    var q = new[] { 1f, 2f, 3f, 4f };
    var k = new[] { 4f, 3f, 2f, 1f };
    var expectedGates = new[] { 1f, 0.5f, 0f, -1f };

    var result = adapter.Apply(q, k, new Pole(1));

    AssertEqual(result.ProjectedQ, ApplyGates(q, expectedGates));
    AssertEqual(result.ProjectedK, ApplyGates(k, expectedGates));
}

static void EffectfulProjectionChangesWitness()
{
    var adapter = CreateAdapter();
    var q = new[] { 1f, 2f, 3f, 4f };
    var result = adapter.Apply(q, q, new Pole(1));

    Assert(result.Receipt.PreWitness != result.Receipt.PostWitness,
        "effectful fixture did not change witness");
}

static void IdentityProjectionPreservesWitness()
{
    var adapter = CreateAdapter();
    var q = new[] { 1f, 2f, 3f, 4f };
    var result = adapter.Apply(q, q, new Pole(0));

    Assert(result.Receipt.PreWitness == result.Receipt.PostWitness,
        "identity fixture changed witness");
}

static void PolicyIdentityChangesWithPolicy()
{
    var q = new[] { 1f, 2f, 3f, 4f };
    var a = CreateAdapter(new float[,] { { 1f, 1f, 1f, 1f } })
        .Apply(q, q, new Pole(0));
    var b = CreateAdapter(new float[,] { { 1f, 1f, 1f, 0.5f } })
        .Apply(q, q, new Pole(0));

    Assert(a.Receipt.PolicyIdentity != b.Receipt.PolicyIdentity,
        "policy identity did not change");
}

static void InputRemainsImmutable()
{
    var adapter = CreateAdapter();
    var q = new[] { 1f, 2f, 3f, 4f };
    var before = q.ToArray();

    _ = adapter.Apply(q, q, new Pole(1));

    AssertEqual(q, before);
}

static void NullableClassUsesDefault()
{
    var adapter = CreateAdapter(classType: _ => null);
    var q = new[] { 1f, 2f, 3f, 4f };

    var result = adapter.Apply(q, q, new Pole(null));
    Assert(result.Receipt.ClassType == 0, "default class was not used");
}

static void LatencyIsNonnegative()
{
    var adapter = CreateAdapter();
    var q = new[] { 1f, 2f, 3f, 4f };

    var result = adapter.Apply(q, q, new Pole(1));
    Assert(result.Receipt.LatencyNs >= 0, "latency was negative");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(IReadOnlyList<T> actual, IReadOnlyList<T> expected)
{
    if (actual.Count != expected.Count)
        throw new InvalidOperationException(
            $"length differs: {actual.Count} != {expected.Count}");

    for (var i = 0; i < actual.Count; i++)
    {
        if (!EqualityComparer<T>.Default.Equals(actual[i], expected[i]))
            throw new InvalidOperationException(
                $"element {i} differs: {actual[i]} != {expected[i]}");
    }
}

internal sealed record Pole(int? Value)
{
    public int? ClassType() => Value;
}
