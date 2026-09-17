using Itoc360;

// Raises an alert and then resolves it, which is the whole lifecycle ITOC360
// cares about. Run it with a source token in the environment:
//
//     ITOC360_TOKEN=... dotnet run --project examples/Basic

var token = Environment.GetEnvironmentVariable("ITOC360_TOKEN");
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("set ITOC360_TOKEN to a source token from the ITOC360 web application");
    return 1;
}

using var client = new Itoc360Client(token);

// The fingerprint is what ties the firing and the resolved event together, so
// it is derived from the condition rather than from this run.
const string fingerprint = "db-01:disk";

try
{
    var raised = await client.SendAlertAsync(new Alert
    {
        Fingerprint = fingerprint,
        Summary = "Disk almost full on db-01",
        Description = "92% of 500G used, growing 4G/hour",
        Severity = Severity.Critical,
        Labels = new Dictionary<string, string>
        {
            ["instance"] = "db-01",
            ["service"] = "postgres",
        },
        GeneratorUrl = "https://grafana.example.com/d/disk",
    });

    Console.WriteLine($"raised   {raised.Type} {raised.Fingerprint}");

    var resolved = await client.SendAlertAsync(new Alert
    {
        Fingerprint = fingerprint,
        Summary = "Disk almost full on db-01",
        Status = Status.Resolved,
    });

    // Both events carry the same fingerprint, so ITOC360 closed the alert the
    // first one opened instead of raising a second.
    Console.WriteLine($"resolved {resolved.Type} {resolved.Fingerprint}");
    return 0;
}
catch (ApiException error) when (error.Unauthorized)
{
    Console.Error.WriteLine("the ITOC360 token is missing or not recognised");
    return 1;
}
catch (Itoc360Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
