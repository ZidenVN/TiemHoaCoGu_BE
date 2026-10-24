using System.Net;
using System.Text.Json;
using HoaSap.DAL;
using HoaSap.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class NotificationReconciliationTests : ApiTestBase
{
    private readonly ApiFixture _fixture;

    public NotificationReconciliationTests(ApiFixture fixture) : base(fixture) => _fixture = fixture;

    [Fact]
    public async Task OnlyAdminCanListUnknownMessages_AndResponseDoesNotExposeDestinationOrPayload()
    {
        var id = await AddMessageAsync(NotificationStatus.Unknown);
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        using var customer = await AuthenticatedAsync(await RegisterAsync($"notify-{Guid.NewGuid():N}@example.test"));

        var response = await admin.GetAsync("/api/management/notifications");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.GetProperty("items")[0];
        Assert.Equal(id.ToString(), item.GetProperty("id").GetString());
        Assert.Equal("Unknown", item.GetProperty("status").GetString());
        Assert.DoesNotContain("+84901234567", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-payload-marker", json, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/management/notifications")).StatusCode);
    }

    [Fact]
    public async Task ProviderNotAcceptedReconciliationRequiresEvidence_AuditsAndQueuesOnlyOneRetry()
    {
        var id = await AddMessageAsync(NotificationStatus.Unknown);
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var body = Json(new { outcome = "not-accepted", evidenceReference = "provider-case-123" });

        var response = await admin.PostAsync($"/api/management/notifications/{id}/reconcile", body);
        response.EnsureSuccessStatusCode();
        using (var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.Equal("Pending", result.RootElement.GetProperty("status").GetString());

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            var row = await db.Set<NotificationOutbox>().AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(NotificationStatus.Pending, row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.Null(row.LeasedUntilUtc);
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.EntityId == id.ToString() && x.Action == "Reconcile");
            Assert.Contains("provider-case-123", audit.NewValuesJson, StringComparison.Ordinal);
            using var oldValues = JsonDocument.Parse(audit.OldValuesJson);
            Assert.Equal(NotificationStatus.Unknown.ToString(), oldValues.RootElement.GetProperty("status").GetString());
        }

        var replay = await admin.PostAsync($"/api/management/notifications/{id}/reconcile", Json(new { outcome = "not-accepted", evidenceReference = "provider-case-123" }));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        await using var verify = _fixture.Services.CreateAsyncScope();
        Assert.Single(await verify.ServiceProvider.GetRequiredService<HoaSapDbContext>().AuditLogs.AsNoTracking()
            .Where(x => x.EntityId == id.ToString() && x.Action == "Reconcile").ToListAsync());
    }

    [Fact]
    public async Task ProviderAcceptedReconciliationMarksSent_AndBadEvidenceIsRejected()
    {
        var id = await AddMessageAsync(NotificationStatus.Unknown);
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());

        var invalid = await admin.PostAsync($"/api/management/notifications/{id}/reconcile", Json(new { outcome = "accepted", evidenceReference = "no" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var response = await admin.PostAsync($"/api/management/notifications/{id}/reconcile", Json(new { outcome = "accepted", evidenceReference = "mailtrap-message-2026-01" }));
        response.EnsureSuccessStatusCode();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().Set<NotificationOutbox>()
            .AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(NotificationStatus.Sent, row.Status);
        Assert.NotNull(row.SentAtUtc);
        Assert.Null(row.LastError);
    }

    private async Task<Guid> AddMessageAsync(NotificationStatus status)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        var row = new NotificationOutbox
        {
            EventType = "OrderChanged",
            Channel = NotificationChannel.Sms,
            Destination = "+84901234567",
            PayloadJson = "{\"marker\":\"sensitive-payload-marker\"}",
            Status = status,
            Attempts = 1,
            LastError = "delivery outcome unknown"
        };
        db.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }
}
