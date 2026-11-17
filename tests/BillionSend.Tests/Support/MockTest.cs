// Tests change environment variables, which every client reads: run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BillionSend.Tests.Support;

/// <summary>A test with its own contract mock, checked after the test for contract violations.</summary>
public abstract class MockTest : IDisposable
{
    public const string Id = Spec.SampleUuid;
    public const string Id2 = "0d5b0a52-8c1f-4a3e-b0f4-7d8e9a1b2c3d";
    public const string Inbox = "support@example.com";

    protected ContractMock Mock { get; } = new();

    protected BillionSendClient Client(Action<BillionSendOptions>? configure = null) => Mock.Client(configure);

    /// <summary>A request body with every property, built from the operation's request schema.</summary>
    protected static JsonNode Body(string key) =>
        Spec.Example(Spec.Get(key).RequestSchema, full: true) ?? throw new InvalidOperationException($"{key} takes no body");

    public virtual void Dispose()
    {
        var violations = Mock.Violations;
        Mock.Dispose();
        GC.SuppressFinalize(this);
        Assert.True(violations.Count == 0, "Contract violations:\n" + string.Join("\n", violations));
    }
}
