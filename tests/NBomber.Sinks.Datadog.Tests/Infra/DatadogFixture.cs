namespace NBomber.Sinks.Datadog.Tests.Infra;

public class DatadogFixture
{
    public string StatsdServerName => "127.0.0.1";
    public int StatsdPort => 8125;
    public PrometheusClient PrometheusClient { get; } = new(new Uri("http://localhost:9090"));
}
