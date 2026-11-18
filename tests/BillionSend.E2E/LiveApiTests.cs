using System.Net;
using BillionSend.Tests.Support;
using Xunit.Abstractions;

namespace BillionSend.E2E;

public sealed class LiveApiTests(ITestOutputHelper output)
{
    public const string ApiUrl = "https://api.billionsend.com";

    /// <summary>The 600 requests a minute of the team's limit are shared with real traffic: one request a second at most.</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(1100);

    private const int ScenarioBudget = 128;
    private const int TotalBudget = 150;

    [LiveFact]
    public async Task RunsEveryScenarioAgainstTheLiveApiAndLeavesNothingBehind()
    {
        var key = Environment.GetEnvironmentVariable(LiveFactAttribute.KeyVariable)!;
        using var network = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var handler = new LiveHandler(network, Gap, ScenarioBudget, TotalBudget);
        var recorder = new Recorder(live: true);
        BillionSendClient NewClient(Action<BillionSendOptions>? configure)
        {
            var options = new BillionSendOptions
            {
                ApiKey = key,
                BaseUrl = ApiUrl,
                HttpClient = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            };
            configure?.Invoke(options);
            return new BillionSendClient(options);
        }

        using var client = NewClient(null);
        var scenarios = new Scenarios(client, NewClient, recorder);
        Exception? aborted = null;
        try
        {
            await scenarios.RunAsync(() => handler.BudgetLeft);
            handler.Cleaning = true; // the rest is the end of the run, and may use what is left of the total
            await scenarios.FinishAsync();
        }
        catch (LiveAbort stop)
        {
            aborted = stop;
        }
        finally
        {
            handler.Cleaning = true;
            await recorder.CleanUp();
            if (aborted is null && !handler.Aborted)
            {
                try
                {
                    await scenarios.VerifyGoneAsync();
                }
                catch (LiveAbort stop)
                {
                    aborted = stop;
                }
            }

            Report(recorder, handler, aborted, scenarios.Tag);
        }

        var problems = new List<string>();
        if (aborted is not null)
        {
            problems.Add($"stopped: {aborted.Message}");
        }

        problems.AddRange(recorder.Leftovers.Select(left => $"left behind: {left}"));
        problems.AddRange(handler.Findings.Distinct().Select(finding => $"finding: {finding}"));
        problems.AddRange(recorder.Results.Where(r => r.Outcome == "FAIL").Select(r => $"step failed: {r.Name}: {r.Detail}"));
        if (handler.Count > TotalBudget)
        {
            problems.Add($"{handler.Count} requests");
        }

        Assert.True(problems.Count == 0, Redact.Text("The live run found:\n" + string.Join("\n", problems)));
    }

    /// <summary>The scenarios themselves, run against the contract mock: they must be well formed, and fit the request budget.</summary>
    [Fact]
    public async Task ScenariosAreWellFormedAndFitTheBudgetWhenRunAgainstTheContractMock()
    {
        using var mock = new ContractMock();
        var recorder = new Recorder(live: false);
        BillionSendClient NewClient(Action<BillionSendOptions>? configure) => mock.Client(configure);
        using var client = NewClient(null);
        var scenarios = new Scenarios(client, NewClient, recorder);
        await scenarios.RunAsync(() => true);
        await scenarios.FinishAsync();
        await recorder.CleanUp();
        await scenarios.VerifyGoneAsync();

        output.WriteLine(recorder.Table());
        Assert.DoesNotContain(recorder.Results, r => r.Outcome == "FAIL");
        Assert.Empty(recorder.Leftovers);
        // The one request the suite makes with a key the account does not know is the 401 check.
        var violations = mock.Violations.Where(v => !v.Contains("expected the API key")).ToList();
        Assert.True(violations.Count == 0, "Contract violations:\n" + string.Join("\n", violations));
        output.WriteLine($"{mock.Hits.Count} requests in the scenarios and the final check");
        Assert.InRange(mock.Hits.Count, 1, TotalBudget - 10);
        Assert.All(mock.Hits, hit => Assert.DoesNotContain("bounce@", hit.Body ?? string.Empty));
        Assert.All(mock.Hits, hit => Assert.DoesNotContain("complaint@", hit.Body ?? string.Empty));
    }

    /// <summary>Nothing can reach an address the account owns: every address a scenario writes is the simulator's success mailbox.</summary>
    [Fact]
    public async Task OnlyTheSimulatorsSuccessAddressIsEverWrittenToARecipientOrAContact()
    {
        using var mock = new ContractMock();
        var recorder = new Recorder(live: false);
        BillionSendClient NewClient(Action<BillionSendOptions>? configure) => mock.Client(configure);
        using var client = NewClient(null);
        var scenarios = new Scenarios(client, NewClient, recorder);
        await scenarios.RunAsync(() => true);
        await scenarios.FinishAsync();
        var addresses = mock.Hits
            .SelectMany(hit => Strings(hit))
            .SelectMany(text => System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[a-z]{2,}").Select(m => m.Value))
            .Distinct()
            .ToList();
        Assert.NotEmpty(addresses);
        Assert.All(
            addresses,
            address => Assert.True(
                address.StartsWith("success+sdk-e2e-dotnet-", StringComparison.Ordinal) && address.EndsWith("@simulator.amazonses.com", StringComparison.Ordinal)
                    || address == "sdk-e2e@mail.billionsend.dev",
                $"unexpected address {address}"));
        Assert.All(
            mock.Hits.Where(hit => hit.Key is "POST /unsubscribes" or "DELETE /oauth/grants/{id}" or "POST /domains" or "POST /domains/claim" or "POST /verifications" or "POST /broadcasts/{id}/send"),
            hit => Assert.Fail($"the suite called {hit.Key}"));
        Assert.DoesNotContain(mock.Hits, hit => hit.Method == "DELETE" && hit.Key is "DELETE /domains/{id}");
        Assert.DoesNotContain(mock.Hits, hit => hit.Key.Contains("/inboxes", StringComparison.Ordinal) || hit.Key.Contains("/emails/receiving", StringComparison.Ordinal));
    }

    /// <summary>Every string a request body carries, decoded (a JSON body's values, or a form's raw text).</summary>
    private static IEnumerable<string> Strings(Hit hit)
    {
        if (hit.Body is null)
        {
            yield break;
        }

        JsonNode? json = null;
        try
        {
            json = hit.Header("content-type")?.StartsWith("application/json", StringComparison.Ordinal) == true ? JsonNode.Parse(hit.Body) : null;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (json is null)
        {
            yield return hit.Body;
            yield break;
        }

        var stack = new Stack<JsonNode?>([json]);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    yield return text;
                    break;
                case JsonObject obj:
                    foreach (var pair in obj)
                    {
                        stack.Push(pair.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        stack.Push(item);
                    }

                    break;
            }
        }
    }

    private void Report(Recorder recorder, LiveHandler handler, Exception? aborted, string tag)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"requests: {handler.Count} (the scenarios were allowed {ScenarioBudget}, everything {TotalBudget})");
        text.AppendLine($"tag: {tag}");
        if (aborted is not null)
        {
            text.AppendLine($"STOPPED: {aborted.Message}");
        }

        text.AppendLine("--- steps");
        text.Append(recorder.Table());
        text.AppendLine("--- requests");
        foreach (var line in handler.Log)
        {
            text.AppendLine(line);
        }

        text.AppendLine("--- findings (the API or the SDK disagrees with the published document)");
        foreach (var finding in handler.Findings.Distinct())
        {
            text.AppendLine(finding);
        }

        text.AppendLine("--- left behind (could not be deleted)");
        foreach (var left in recorder.Leftovers)
        {
            text.AppendLine(left);
        }

        var report = Redact.Text(text.ToString());
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("BILLIONSEND_E2E_REPORT") is { Length: > 0 } path)
        {
            File.WriteAllText(path, report);
        }
    }
}
