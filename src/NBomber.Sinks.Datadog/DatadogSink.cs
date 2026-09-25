using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Metrics;
using NBomber.Contracts.Stats;
using Serilog;
using StatsdClient;

namespace NBomber.Sinks.Datadog;

/// <summary>
/// Represents the configuration settings for sending metrics to a Datadog StatsD server.
/// </summary>
public class DatadogSinkConfig
{
    /// <summary>
    /// Gets or sets the hostname or IP address of the StatsD server.
    /// Defaults to <c>"127.0.0.1"</c>.
    /// </summary>
    public string StatsdServerName { get; set; } = "127.0.0.1";
    
    /// <summary>
    /// Gets or sets the port number used to communicate with the StatsD server.
    /// Defaults to <c>8125</c>.
    /// </summary>
    public int StatsdPort { get; set; } = 8125;
}

/// <summary>
/// A reporting sink implementation for sending performance metrics to Datadog via StatsD.
/// </summary>
public class DatadogSink : IReportingSink
{
    private ILogger _logger;
    private readonly DogStatsdService _datadogClient = new();
    private IBaseContext _context;
    private StatsdConfig _statsdConfig = new();

    // Tags are stable for the whole test session, so they are built once and then reused.
    // Step tags are cached per (scenario, step). The only part that changes per metric is
    // the "status_code_status" tag, which is appended to the cached scenario tags via Array.CopyTo.
    private OperationType? _cachedOperationType;
    private Dictionary<string, string> _globalTags = null;
    private readonly ConcurrentDictionary<string, string[]> _scenarioTags = new();
    private readonly ConcurrentDictionary<(string Scenario, string Step), string[]> _stepTags = new();
    private readonly ConcurrentDictionary<string, string[]> _metricTags = new();

    /// <summary>
    /// Gets the name of the sink, used for identification within NBomber.
    /// </summary>
    public string SinkName => "NBomber.Sinks.Datadog";

    /// <summary>
    /// Gets the underlying <see cref="DogStatsdService"/> used to write metrics.
    /// </summary>
    public DogStatsdService DatadogClient => _datadogClient;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="DatadogSink"/> class with default configuration.
    /// </summary>
    public DatadogSink() 
    { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatadogSink"/> class using the specified <see cref="DatadogSinkConfig"/>.
    /// </summary>
    /// <param name="config">The configuration object containing Datadog StatsD connection details.</param>
    public DatadogSink(DatadogSinkConfig config)
    {
        _statsdConfig = MapConfig(config);
    }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="DatadogSink"/> class using a pre-built <see cref="StatsdConfig"/>.
    /// </summary>
    /// <param name="statsdConfig">The StatsD configuration object.</param>
    public DatadogSink(StatsdConfig statsdConfig)
    {
        _statsdConfig = statsdConfig;
    }
    
    /// <summary>
    /// Initializes the Datadog sink with context and optional infrastructure configuration.
    /// </summary>
    /// <param name="context">The base context provided by NBomber.</param>
    /// <param name="infraConfig">Optional infrastructure configuration that may contain Datadog sink settings.</param>
    /// <returns>A completed task if initialization succeeds; otherwise throws an exception.</returns>
    /// <exception cref="InvalidOperationException">Thrown if Datadog client fails to configure properly.</exception>
    public Task Init(IBaseContext context, IConfiguration infraConfig)
    {
        _logger = context.Logger.ForContext<DatadogSink>();
        _context = context;
        
        var config = infraConfig?.GetSection("DatadogSink").Get<DatadogSinkConfig>();
        if (config != null)
        {
            _statsdConfig = MapConfig(config);
        }

        if (!_datadogClient.Configure(_statsdConfig))
        {
            _logger.Error("Reporting Sink {0} has problems with initialization. The problem could be related to invalid config structure.", SinkName);
            throw new InvalidOperationException($"Cannot initialize {SinkName}. Please check the configuration.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the reporting sink at the beginning of a test session.
    /// This method is called at the start of the test and allows the sink to perform any necessary preparations before data collection begins.
    /// </summary>
    /// <param name="sessionInfo">Contains metadata about the test session and scenarios that will be executed.</param> 
    public Task Start(SessionStartInfo sessionInfo)
    {
        ClearTagsCache();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves real-time performance statistics during the test run.
    /// This method is invoked periodically based on the configured <c>ReportingInterval</c> to capture intermediate metrics.
    /// </summary>
    /// <param name="stats">Real-time stats data of the running scenarios.</param>
    public Task SaveRealtimeStats(ScenarioStats[] stats)
    {
        var updatedStats = stats.Select(AddGlobalInfoStep).ToArray();
        
        SaveStats(updatedStats, OperationType.Bombing);
        _datadogClient.Flush();
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves custom metrics collected during scenario execution.
    /// This method is invoked periodically based on the configured <c>ReportingInterval</c>,
    /// allowing the reporting sink to persist user-defined metrics such as counters, gauges, or other performance indicators.
    /// </summary>
    /// <param name="metrics">A collection of metrics captured during the test session.</param>
    /// <returns>A task that represents the asynchronous operation of saving the metrics.</returns>
    public Task SaveRealtimeMetrics(MetricStats metrics)
    {
        SaveMetrics(metrics, OperationType.Bombing);
        _datadogClient.Flush();
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves the final scenario statistics after the test session completes.
    /// </summary>
    /// <param name="stats">The final node statistics to send to Datadog.</param>
    /// <returns>A completed task.</returns>
    public Task SaveFinalStats(NodeStats stats)
    {
        var updatedStats = stats.ScenarioStats.Select(AddGlobalInfoStep).ToArray();
        
        SaveStats(updatedStats, OperationType.Complete);
        SaveMetrics(stats.Metrics, OperationType.Complete);
        _datadogClient.Flush();
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the reporting sink and releases any held resources (e.g., network or database connections).
    /// This method is invoked once the test session ends and should perform any necessary cleanup.
    /// </summary> 
    public Task Stop()
    {
        _datadogClient.Flush();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases all resources used by the <see cref="DatadogSink"/> instance,
    /// including flushing and disposing the Datadog client.
    /// </summary>
    public void Dispose()
    {
        _datadogClient.Flush();
        _datadogClient.Dispose();
    }
    
    private StatsdConfig MapConfig(DatadogSinkConfig config)
    {
        return new StatsdConfig
        {
            StatsdServerName = config.StatsdServerName,
            StatsdPort = config.StatsdPort
        };
    }

    private void SaveMetrics(MetricStats stats, OperationType operationType)
    {
        EnsureTagCache(operationType);

        foreach (var counter in stats.Counters)
        {
            _datadogClient.Gauge($"nbomber.counters.{counter.MetricName}", counter.Value, tags: GetMetricTags(operationType, counter.ScenarioName));
        }

        foreach (var gauge in stats.Gauges)
        {
            _datadogClient.Gauge($"nbomber.gauges.{gauge.MetricName}", gauge.Value, tags: GetMetricTags(operationType, gauge.ScenarioName));
        }
    }
    
    private void SaveStats(ScenarioStats[] stats, OperationType operationType)
    {
        EnsureTagCache(operationType);

        foreach (var scenario in stats)
        {
            var simulation = scenario.LoadSimulationStats;

            foreach (var step in scenario.StepStats)
            {
                var tags = GetStepTags(operationType, scenario, step.StepName);

                var okR = step.Ok.Request;
                var okL = step.Ok.Latency;
                var okD = step.Ok.DataTransfer;

                var fR = step.Fail.Request;
                var fL = step.Fail.Latency;
                var fD = step.Fail.DataTransfer;
                
                _datadogClient.Gauge("nbomber.all.request.count", step.Ok.Request.Count + step.Fail.Request.Count, tags: tags);
                _datadogClient.Gauge("nbomber.all.datatransfer.all", step.Ok.DataTransfer.AllBytes + step.Fail.DataTransfer.AllBytes, tags: tags);

                // OK
                _datadogClient.Gauge("nbomber.ok.request.count", okR.Count, tags: tags);
                _datadogClient.Gauge("nbomber.ok.request.rps", okR.RPS, tags: tags);

                _datadogClient.Gauge("nbomber.ok.latency.min", okL.MinMs, tags: tags);
                _datadogClient.Gauge("nbomber.ok.latency.mean", okL.MeanMs, tags: tags);
                _datadogClient.Gauge("nbomber.ok.latency.max", okL.MaxMs, tags: tags);
                _datadogClient.Gauge("nbomber.ok.latency.stddev", okL.StdDev, tags: tags);
                _datadogClient.Gauge("nbomber.ok.latency.percent50", okL.Percent50, tags: tags);
                _datadogClient.Gauge("nbomber.ok.latency.percent75", okL.Percent75, tags: tags);
                _datadogClient.Gauge("nbomber.ok.latency.percent95", okL.Percent95, tags: tags);
                _datadogClient.Gauge("nbomber.ok.latency.percent99", okL.Percent99, tags: tags);

                _datadogClient.Gauge("nbomber.ok.datatransfer.min", okD.MinBytes, tags: tags);
                _datadogClient.Gauge("nbomber.ok.datatransfer.mean", okD.MeanBytes, tags: tags);
                _datadogClient.Gauge("nbomber.ok.datatransfer.max", okD.MaxBytes, tags: tags);
                _datadogClient.Gauge("nbomber.ok.datatransfer.all", okD.AllBytes, tags: tags);
                _datadogClient.Gauge("nbomber.ok.datatransfer.percent50", okD.Percent50, tags: tags);
                _datadogClient.Gauge("nbomber.ok.datatransfer.percent75", okD.Percent75, tags: tags);
                _datadogClient.Gauge("nbomber.ok.datatransfer.percent95", okD.Percent95, tags: tags);
                _datadogClient.Gauge("nbomber.ok.datatransfer.percent99", okD.Percent99, tags: tags);

                // FAIL
                _datadogClient.Gauge("nbomber.fail.request.count", fR.Count, tags: tags);
                _datadogClient.Gauge("nbomber.fail.request.rps", fR.RPS, tags: tags);

                _datadogClient.Gauge("nbomber.fail.latency.min", fL.MinMs, tags: tags);
                _datadogClient.Gauge("nbomber.fail.latency.mean", fL.MeanMs, tags: tags);
                _datadogClient.Gauge("nbomber.fail.latency.max", fL.MaxMs, tags: tags);
                _datadogClient.Gauge("nbomber.fail.latency.stddev", fL.StdDev, tags: tags);
                _datadogClient.Gauge("nbomber.fail.latency.percent50", fL.Percent50, tags: tags);
                _datadogClient.Gauge("nbomber.fail.latency.percent75", fL.Percent75, tags: tags);
                _datadogClient.Gauge("nbomber.fail.latency.percent95", fL.Percent95, tags: tags);
                _datadogClient.Gauge("nbomber.fail.latency.percent99", fL.Percent99, tags: tags);

                _datadogClient.Gauge("nbomber.fail.datatransfer.min", fD.MinBytes, tags: tags);
                _datadogClient.Gauge("nbomber.fail.datatransfer.mean", fD.MeanBytes, tags: tags);
                _datadogClient.Gauge("nbomber.fail.datatransfer.max", fD.MaxBytes, tags: tags);
                _datadogClient.Gauge("nbomber.fail.datatransfer.all", fD.AllBytes, tags: tags);
                _datadogClient.Gauge("nbomber.fail.datatransfer.percent50", fD.Percent50, tags: tags);
                _datadogClient.Gauge("nbomber.fail.datatransfer.percent75", fD.Percent75, tags: tags);
                _datadogClient.Gauge("nbomber.fail.datatransfer.percent95", fD.Percent95, tags: tags);
                _datadogClient.Gauge("nbomber.fail.datatransfer.percent99", fD.Percent99, tags: tags);

                _datadogClient.Gauge("nbomber.simulation.value", simulation.Value, tags: tags);
            }
            
            SaveStatusCodes(scenario, operationType);
        }
    }
    
    private void SaveStatusCodes(ScenarioStats scnStats, OperationType operationType)
    {
        var scenarioTags = GetScenarioTags(operationType, scnStats);
        var statusCodes = scnStats.Ok.StatusCodes.Concat(scnStats.Fail.StatusCodes);

        foreach (var s in statusCodes)
        {
            var tags = AppendTag(scenarioTags, "status_code_status", s.StatusCode);

            _datadogClient.Gauge("nbomber.status_code.count", s.Count, tags: tags);
        }
    }

    private ScenarioStats AddGlobalInfoStep(ScenarioStats scnStats)
    {
        var globalStepInfo = new StepStats("global information", scnStats.Ok, scnStats.Fail, sortIndex: 0);
        scnStats.StepStats = [.. scnStats.StepStats, globalStepInfo];

        return scnStats;
    }

    /// <summary>
    /// Drops the cached tags if they were built for a different operation type.
    /// The operation type changes at most once per session (Bombing -> Complete).
    /// </summary>
    private void EnsureTagCache(OperationType operationType)
    {
        if (_cachedOperationType == operationType) return;

        ClearTagsCache();
        _cachedOperationType = operationType;
    }

    private void ClearTagsCache()
    {
        _cachedOperationType = null;
        _globalTags = null;
        _scenarioTags.Clear();
        _stepTags.Clear();
        _metricTags.Clear();
    }

    private Dictionary<string, string> GetGlobalTags(OperationType operationType) =>
        _globalTags ??= BuildGlobalTags(operationType);

    private string[] GetScenarioTags(OperationType operationType, ScenarioStats scnStats) =>
        _scenarioTags.GetOrAdd(scnStats.ScenarioName,
            static (_, state) => state.Sink.BuildScenarioTags(state.OperationType, state.ScnStats),
            (Sink: this, OperationType: operationType, ScnStats: scnStats));

    private string[] GetStepTags(OperationType operationType, ScenarioStats scnStats, string stepName) =>
        _stepTags.GetOrAdd((scnStats.ScenarioName, stepName),
            static (key, state) =>
            {
                var scnTags = state.Sink.GetScenarioTags(state.OperationType, state.ScnStats);
                return AppendTag(scnTags, "step", key.Step);
            },
            (Sink: this, OperationType: operationType, ScnStats: scnStats));

    private string[] GetMetricTags(OperationType operationType, string scenarioName) =>
        _metricTags.GetOrAdd(scenarioName,
            static (scnName, state) => state.Sink.BuildMetricTags(state.OperationType, scnName),
            (Sink: this, OperationType: operationType));

    private Dictionary<string, string> BuildGlobalTags(OperationType operationType)
    {
        Dictionary<string, string> BuildSessionDefaultTags(OperationType operation)
        {
            var nodeInfo = _context.GetNodeInfo();
            var testInfo = _context.TestInfo;

            return new Dictionary<string, string>
            {
                ["session_id"] = testInfo.SessionId,
                ["operation_type"] = operation.ToString(),
                ["node_type"] = nodeInfo.NodeType.ToString(),
                ["test_suite"] = testInfo.TestSuite,
                ["test_name"] = testInfo.TestName
            };
        }

        var tags = BuildSessionDefaultTags(operationType);
        AddTags(tags, _context.TestInfo.Tags);

        return tags;
    }

    private string[] BuildScenarioTags(OperationType operationType, ScenarioStats scnStats)
    {
        var globalTags = GetGlobalTags(operationType);
        
        var tags = new Dictionary<string, string>(globalTags)
        {
            ["scenario"] = scnStats.ScenarioName
        };

        AddTags(tags, scnStats.Tags);

        return MapTags(tags);
    }

    private string[] BuildMetricTags(OperationType operationType, string scenarioName)
    {
        var globalTags = GetGlobalTags(operationType);

        if (string.IsNullOrEmpty(scenarioName))
            return MapTags(globalTags);

        var tags = new Dictionary<string, string>(globalTags)
        {
            ["scenario"] = scenarioName
        };

        return MapTags(tags);
    }

    private void AddTags(Dictionary<string, string> target, IReadOnlyDictionary<string, string> tags)
    {
        foreach (var tag in tags)
            target[tag.Key] = tag.Value;
    }

    private string[] MapTags(Dictionary<string, string> tags) =>
        tags.Select(t => $"{t.Key}:{t.Value}").ToArray();

    private static string[] AppendTag(string[] tags, string key, string value)
    {
        var result = new string[tags.Length + 1];
        tags.CopyTo(result, 0);
        result[tags.Length] = $"{key}:{value}";

        return result;
    }
}
