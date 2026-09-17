using System.Text.Json.Nodes;
using Xunit;

namespace Itoc360.Tests;

public class ValidateTests
{
    private static Alert Valid() => new() { Fingerprint = "db-01:disk", Summary = "Disk almost full" };

    [Fact]
    public void AcceptsAnAlertWithBothRequiredFields()
    {
        AlertPayload.Validate(Valid());
    }

    [Fact]
    public void RejectsAnEmptyFingerprint()
    {
        var alert = new Alert { Fingerprint = "", Summary = "Disk almost full" };

        var error = Assert.Throws<ValidationException>(() => AlertPayload.Validate(alert));

        Assert.Equal("fingerprint", error.Field);
        Assert.Contains("deduplicates", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnEmptySummary()
    {
        var alert = new Alert { Fingerprint = "db-01:disk", Summary = "" };

        var error = Assert.Throws<ValidationException>(() => AlertPayload.Validate(alert));

        Assert.Equal("summary", error.Field);
    }

    [Theory]
    [InlineData(Status.Firing)]
    [InlineData(Status.Resolved)]
    public void AcceptsTheTwoStatusesItoc360Understands(string status)
    {
        AlertPayload.Validate(new Alert
        {
            Fingerprint = "db-01:disk",
            Summary = "Disk almost full",
            Status = status,
        });
    }

    [Fact]
    public void RejectsAnyOtherStatus()
    {
        var alert = new Alert
        {
            Fingerprint = "db-01:disk",
            Summary = "Disk almost full",
            Status = "FIRING",
        };

        var error = Assert.Throws<ValidationException>(() => AlertPayload.Validate(alert));

        Assert.Equal("status", error.Field);
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AlertPayload.Validate(null!));
    }
}

public class BuildTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static JsonObject FirstAlert(JsonObject payload) =>
        (JsonObject)payload["alerts"]!.AsArray()[0]!;

    [Fact]
    public void AppliesTheDocumentedDefaults()
    {
        var payload = AlertPayload.Build(
            new Alert { Fingerprint = "db-01:disk", Summary = "Disk almost full" }, Now);

        Assert.Equal("firing", (string?)payload["status"]);
        Assert.Equal("itoc360-dotnet", (string?)payload["receiver"]);
        Assert.Equal("4", (string?)payload["version"]);
        Assert.Equal("db-01:disk", (string?)payload["groupKey"]);
        Assert.Equal("warning", (string?)payload["commonLabels"]!["severity"]);
    }

    [Fact]
    public void PutsTheSummaryWhereItoc360ReadsTheTitle()
    {
        var payload = AlertPayload.Build(
            new Alert { Fingerprint = "f", Summary = "Disk almost full" }, Now);

        Assert.Equal("Disk almost full", (string?)payload["commonAnnotations"]!["summary"]);
        Assert.Equal("Disk almost full", (string?)FirstAlert(payload)["annotations"]!["summary"]);
    }

    [Fact]
    public void OmitsTheDescriptionWhenThereIsNone()
    {
        var payload = AlertPayload.Build(
            new Alert { Fingerprint = "f", Summary = "s" }, Now);

        Assert.Null(payload["commonAnnotations"]!["description"]);
    }

    [Fact]
    public void CarriesTheDescriptionWhenThereIsOne()
    {
        var payload = AlertPayload.Build(
            new Alert { Fingerprint = "f", Summary = "s", Description = "92% of 500G used" }, Now);

        Assert.Equal("92% of 500G used", (string?)payload["commonAnnotations"]!["description"]);
    }

    [Fact]
    public void LetsTheSeverityFieldWinOverALabelOfTheSameName()
    {
        var payload = AlertPayload.Build(
            new Alert
            {
                Fingerprint = "f",
                Summary = "s",
                Severity = Severity.Critical,
                Labels = new Dictionary<string, string> { ["severity"] = "info", ["team"] = "sre" },
            },
            Now);

        Assert.Equal("critical", (string?)payload["commonLabels"]!["severity"]);
        Assert.Equal("sre", (string?)payload["commonLabels"]!["team"]);
    }

    [Fact]
    public void KeepsCallerAnnotations()
    {
        var payload = AlertPayload.Build(
            new Alert
            {
                Fingerprint = "f",
                Summary = "s",
                Annotations = new Dictionary<string, string> { ["runbook"] = "https://wiki/disk" },
            },
            Now);

        Assert.Equal("https://wiki/disk", (string?)payload["commonAnnotations"]!["runbook"]);
    }

    [Fact]
    public void StampsStartsAtWithTheClockWhenTheAlertLeavesItUnset()
    {
        var payload = AlertPayload.Build(new Alert { Fingerprint = "f", Summary = "s" }, Now);

        Assert.Equal("2026-03-04T05:06:07Z", (string?)FirstAlert(payload)["startsAt"]);
    }

    [Fact]
    public void RendersTimestampsAsUtcRegardlessOfTheOffsetGiven()
    {
        var payload = AlertPayload.Build(
            new Alert
            {
                Fingerprint = "f",
                Summary = "s",
                StartsAt = new DateTimeOffset(2026, 3, 4, 8, 6, 7, TimeSpan.FromHours(3)),
            },
            Now);

        Assert.Equal("2026-03-04T05:06:07Z", (string?)FirstAlert(payload)["startsAt"]);
    }

    [Fact]
    public void StampsEndsAtOnAResolvedAlert()
    {
        var payload = AlertPayload.Build(
            new Alert { Fingerprint = "f", Summary = "s", Status = Status.Resolved }, Now);

        Assert.Equal("2026-03-04T05:06:07Z", (string?)FirstAlert(payload)["endsAt"]);
    }

    [Fact]
    public void LeavesEndsAtOffAFiringAlertThatDidNotSetOne()
    {
        var payload = AlertPayload.Build(new Alert { Fingerprint = "f", Summary = "s" }, Now);

        Assert.Null(FirstAlert(payload)["endsAt"]);
    }

    [Fact]
    public void KeepsAnExplicitEndsAtOnAFiringAlert()
    {
        var payload = AlertPayload.Build(
            new Alert
            {
                Fingerprint = "f",
                Summary = "s",
                EndsAt = new DateTimeOffset(2026, 3, 4, 6, 0, 0, TimeSpan.Zero),
            },
            Now);

        Assert.Equal("2026-03-04T06:00:00Z", (string?)FirstAlert(payload)["endsAt"]);
    }

    [Fact]
    public void CarriesTheGeneratorUrlUnderTheNameAlertmanagerUses()
    {
        var payload = AlertPayload.Build(
            new Alert { Fingerprint = "f", Summary = "s", GeneratorUrl = "https://grafana/d/1" },
            Now);

        Assert.Equal("https://grafana/d/1", (string?)FirstAlert(payload)["generatorURL"]);
    }

    [Fact]
    public void OmitsTheGeneratorUrlWhenThereIsNone()
    {
        var payload = AlertPayload.Build(new Alert { Fingerprint = "f", Summary = "s" }, Now);

        Assert.Null(FirstAlert(payload)["generatorURL"]);
    }

    [Fact]
    public void LetsTheCallerNameTheSender()
    {
        var payload = AlertPayload.Build(
            new Alert { Fingerprint = "f", Summary = "s", Receiver = "billing-worker" }, Now);

        Assert.Equal("billing-worker", (string?)payload["receiver"]);
    }

    [Fact]
    public void GroupsOnTheFingerprintSoItoc360CanDeduplicate()
    {
        var payload = AlertPayload.Build(
            new Alert { Fingerprint = "db-01:disk", Summary = "s" }, Now);

        Assert.Equal("db-01:disk", (string?)payload["groupLabels"]!["fingerprint"]);
        Assert.Equal("db-01:disk", (string?)FirstAlert(payload)["fingerprint"]);
    }

    [Fact]
    public void SendsExactlyOneAlertPerCall()
    {
        var payload = AlertPayload.Build(new Alert { Fingerprint = "f", Summary = "s" }, Now);

        Assert.Single(payload["alerts"]!.AsArray());
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AlertPayload.Build(null!));
    }
}
