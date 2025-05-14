using NBomber.CSharp;using NBomber.Sinks.Datadog;

var scn = Scenario.Create("scn", async ctx =>
{
    await Step.Run("my_step", ctx, async () =>
    {
        await Task.Delay(1_000);
        return Response.Ok(statusCode: "300");
    });
    
    return Response.Ok(statusCode: "400");
})
.WithoutWarmUp();

var datadog = new DatadogSink();

NBomberRunner
    .RegisterScenarios(scn)
    .WithReportingSinks(datadog)
    .Run();