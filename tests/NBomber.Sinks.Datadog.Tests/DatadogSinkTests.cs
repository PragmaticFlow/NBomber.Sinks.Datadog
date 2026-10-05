using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using NBomber.Sinks.Datadog.Tests.Infra;
using Shouldly;

namespace NBomber.Sinks.Datadog.Tests;

public class DatadogSinkTests(DatadogFixture fixture) : IClassFixture<DatadogFixture>
{
    private const string ScenarioName = "e2e_scenario";
    private const string StepName = "step_1";
    private const string CounterName = "e2e-custom-counter";
    private const string GaugeName = "e2e-custom-gauge";
    private const double GaugeValue = 42.5;

    [Fact]
    public async Task ReportingSink_should_write_final_scenario_stats()
    {
        var testName = CreateTestName();
        var sink = CreateSink();

        var stats = RunLoadTest(sink, testName);

        var scnStats = stats.ScenarioStats.Get(ScenarioName);
        var stepStats = scnStats.StepStats.First(x => x.StepName == StepName);
        scnStats.Ok.Request.Count.ShouldBeGreaterThan(0);

        var scenarioTags = GenerateTags(testName, OperationType.Complete, new() { ["step"] = "global information" });
        var stepTags = GenerateTags(testName, OperationType.Complete, new() { ["step"] = StepName });

        var scnOkCount = await fixture.PrometheusClient.WaitForSample("nbomber.ok.request.count", scenarioTags);
        scnOkCount.Value.ShouldBe(scnStats.Ok.Request.Count);

        var scnFailCount = await fixture.PrometheusClient.WaitForSample("nbomber.fail.request.count", scenarioTags);
        scnFailCount.Value.ShouldBe(scnStats.Fail.Request.Count);

        var stepOkCount = await fixture.PrometheusClient.WaitForSample("nbomber.ok.request.count", stepTags);
        stepOkCount.Value.ShouldBe(stepStats.Ok.Request.Count);

        var stepLatencyMax = await fixture.PrometheusClient.WaitForSample("nbomber.ok.latency.max", stepTags);
        stepLatencyMax.Value.ShouldBe(stepStats.Ok.Latency.MaxMs, tolerance: 0.001);

        var statusCodeTags = GenerateTags(testName, OperationType.Complete, 
            new() { ["status_code_status"] = "200", ["step"] = "global information" });

        var statusCodeCount = await fixture.PrometheusClient.WaitForSample("nbomber.status_code.count", statusCodeTags);
        statusCodeCount.Value.ShouldBe(scnStats.Ok.StatusCodes.First(x => x.StatusCode == "200").Count);
    }

    [Fact]
    public async Task ReportingSink_should_write_realtime_stats()
    {
        var testName = CreateTestName();
        var sink = CreateSink();

        RunLoadTest(sink, testName);

        var tags = GenerateTags(testName, OperationType.Bombing, new() { ["step"] = StepName });

        var okCount = await fixture.PrometheusClient.WaitForSample("nbomber.ok.request.count", tags);
        okCount.Value.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ReportingSink_should_write_custom_metrics()
    {
        var testName = CreateTestName();
        var sink = CreateSink();

        var stats = RunLoadTest(sink, testName);

        var counterStats = stats.Metrics.Counters.First(x => x.MetricName == CounterName);
        var gaugeStats = stats.Metrics.Gauges.First(x => x.MetricName == GaugeName);
        counterStats.Value.ShouldBeGreaterThan(0);

        var tags = GenerateTags(testName, OperationType.Complete);

        var counter = await fixture.PrometheusClient.WaitForSample($"nbomber.counters.{CounterName}", tags);
        counter.Value.ShouldBe(counterStats.Value);

        var gauge = await fixture.PrometheusClient.WaitForSample($"nbomber.gauges.{GaugeName}", tags);
        gaugeStats.Value.ShouldBe(GaugeValue);
        gauge.Value.ShouldBe(gaugeStats.Value);
    }

    private static NodeStats RunLoadTest(DatadogSink sink, string testName)
    {
        var counter = Metric.CreateCounter(CounterName, unitOfMeasure: "MB");
        var gauge = Metric.CreateGauge(GaugeName, unitOfMeasure: "KB");

        var scenario = Scenario.Create(ScenarioName, async context =>
        {
            await Step.Run(StepName, context, async () =>
            {
                await Task.Delay(10);

                counter.Add(1);
                gauge.Set(GaugeValue);

                return Response.Ok(statusCode: "200", sizeBytes: 100);
            });

            return Response.Ok();
        })
        .WithInit(ctx =>
        {
            ctx.RegisterMetric(counter);
            ctx.RegisterMetric(gauge);
            return Task.CompletedTask;
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            // longer than the reporting interval, so at least one realtime report is sent
            Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(7))
        );

        return NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("e2e")
            .WithTestName(testName)
            .WithReportingInterval(TimeSpan.FromSeconds(5))
            .WithoutReports()
            .WithReportingSinks(sink)
            .Run();
    }

    private DatadogSink CreateSink()
    {
        var config = new DatadogSinkConfig
        {
            StatsdServerName = fixture.StatsdServerName,
            StatsdPort = fixture.StatsdPort
        };

        return new DatadogSink(config);
    }

    private static string CreateTestName() => $"datadog_{Guid.NewGuid():N}";

    private static Dictionary<string, string> GenerateTags(string testName, OperationType operationType,
        Dictionary<string, string>? customTags = null)
    {
        var tags = new Dictionary<string, string>
        {
            ["test_suite"] = "e2e",
            ["test_name"] = testName,
            ["scenario"] = ScenarioName,
            ["operation_type"] = operationType.ToString()
        };

        if (customTags != null)
        {
            foreach (var (key, value) in customTags)
                tags[key] = value;
        }

        return tags;
    }
}
