using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace NBomber.Sinks.Datadog.Tests.Infra;

public record MetricSample(IReadOnlyDictionary<string, string> Labels, double Value);

public class PrometheusClient(Uri prometheusEndpoint)
{
    private static readonly HttpClient Http = new();

    public static string ToPrometheusName(string datadogName) =>
        datadogName.Replace('.', '_').Replace('-', '_') + "_value";

    public async Task<IReadOnlyList<MetricSample>> Query(string promQl)
    {
        var url = new Uri(prometheusEndpoint, $"/api/v1/query?query={Uri.EscapeDataString(promQl)}");
        var response = await Http.GetFromJsonAsync<QueryResponse>(url, JsonSerializerOptions.Web);

        return response!.Data.Result
            .Select(x => new MetricSample(x.Metric, double.Parse(x.Value[1].GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
    }

    public async Task<MetricSample> WaitForSample(string datadogMetricName, IReadOnlyDictionary<string, string> tags,
        TimeSpan? timeout = null)
    {
        var promQl = BuildSelector(ToPrometheusName(datadogMetricName), tags);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));

        while (true)
        {
            var samples = await Query(promQl);
            if (samples.Count > 1)
                throw new InvalidOperationException($"Query '{promQl}' returned {samples.Count} series, expected one.");

            if (samples.Count == 1)
                return samples[0];

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Query '{promQl}' returned no data from Prometheus.");

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    // e.g. nbomber_ok_request_count_value{scenario="e2e_scenario",step="step_1"}
    private static string BuildSelector(string metricName, IReadOnlyDictionary<string, string> tags)
    {
        var matchers = tags.Select(label => $"{label.Key}=\"{label.Value}\"");
        return metricName + "{" + string.Join(",", matchers) + "}";
    }

    private record QueryResponse(string Status, QueryData Data);

    private record QueryData(string ResultType, QueryResult[] Result);

    // value is a [unixTime, "stringValue"] pair
    private record QueryResult(Dictionary<string, string> Metric, JsonElement[] Value);
}
