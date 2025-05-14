using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
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
    private DogStatsdService _datadogClient = new();
    private IBaseContext _context;
    private StatsdConfig _statsdConfig = new();

    /// <summary>
    /// Gets the name of the sink, used for identification within NBomber.
    /// </summary>
    public string SinkName => "NBomber.Sinks.Datadog";

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
            throw new InvalidOperationException($"Cannot initialize {nameof(DatadogSink)}. Please check the configuration.");
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called at the start of a test session.
    /// </summary>
    /// <param name="sessionInfo">Session metadata and configuration information.</param>
    /// <returns>A completed task.</returns>
    public Task Start(SessionStartInfo sessionInfo)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves real-time scenario statistics during the test execution.
    /// </summary>
    /// <param name="stats">An array of scenario statistics to send to Datadog.</param>
    /// <returns>A completed task.</returns>
    public Task SaveRealtimeStats(ScenarioStats[] stats)
    {
        var updatedStats = stats.Select(AddGlobalInfoStep).ToArray();
        SaveStats(updatedStats, OperationType.Bombing);
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
        _datadogClient.Flush();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Finalizes the reporting sink. This method is called at the end of the test run.
    /// </summary>
    /// <returns>A completed task.</returns>
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
    
    private void SaveStats(ScenarioStats[] stats, OperationType operationType)
    {
        foreach (var scenario in stats)
        {
            var simulation = scenario.LoadSimulationStats;
            
            foreach (var step in scenario.StepStats)
            {
                var testInfo = _context.TestInfo;
                var tags = new[]
                {
                    $"test_suite:{testInfo.TestSuite}",
                    $"test_name:{testInfo.TestName}",
                    $"scenario:{scenario.ScenarioName}", 
                    $"step:{step.StepName}",
                    $"operation_type:{operationType}"
                };
                    
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
        var statusCodes = scnStats.Ok.StatusCodes.Concat(scnStats.Fail.StatusCodes);
        
        var testInfo = _context.TestInfo;
        
        foreach (var s in statusCodes)
        {
            var tags = new[]
            {
                $"test_suite:{testInfo.TestSuite}",
                $"test_name:{testInfo.TestName}",
                $"scenario:{scnStats.ScenarioName}",
                $"operation_type:{operationType}",
                $"status_code_status:{s.StatusCode}"
            };
            
            _datadogClient.Gauge("nbomber.status_code.count", s.Count, tags: tags);
        }
    }
    
    private ScenarioStats AddGlobalInfoStep(ScenarioStats scnStats)
    {
        var globalStepInfo = new StepStats("global information", scnStats.Ok, scnStats.Fail, sortIndex: 0);
        scnStats.StepStats = scnStats.StepStats.Append(globalStepInfo).ToArray();
            
        return scnStats;
    }
}
