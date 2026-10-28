using System.Text.Json;
using HoaSap.API.Services;
using HoaSap.DAL;
using HoaSap.DAL.Notifications;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;
using Xunit;

namespace HoaSap.Tests;

public sealed class NotificationSecurityTests
{
    private static HoaSapDbContext Db(SqliteConnection connection) => new(new DbContextOptionsBuilder<HoaSapDbContext>().UseSqlite(connection).Options);

    [Fact]
    public async Task StagingOnlyEmitsStatusTransitionsWithRealDestinations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Db(connection); await db.Database.EnsureCreatedAsync();
        var customer = new Customer { Email = "customer@example.test", PhoneNumber = "+84901234567", FullName = "Customer" };
        var order = new Order { CustomerId = customer.Id, OrderCode = "O-1", OrderStatus = "Confirmed", Note = "initial" };
        db.Customers.Add(customer); db.Orders.Add(order); await db.SaveChangesAsync();
        db.Entry(order).Property(x => x.Note).CurrentValue = "changed"; await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<NotificationOutbox>().CountAsync());
        db.Entry(order).Property(x => x.OrderStatus).CurrentValue = "Delivered"; await db.SaveChangesAsync();
        var rows = await db.Set<NotificationOutbox>().Where(x => x.EventType == "OrderChanged").ToListAsync();
        Assert.Equal(4, rows.Count); Assert.Contains(rows, x => x.Channel == NotificationChannel.Email && x.Destination == customer.Email); Assert.Contains(rows, x => x.Channel == NotificationChannel.Sms && x.Destination == customer.PhoneNumber);
    }

    [Fact]
    public async Task PasswordResetStagingDoesNotPersistRawToken()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Db(connection); await db.Database.EnsureCreatedAsync();
        var customer = new Customer { Email = "customer@example.test", PhoneNumber = "+84901234567", FullName = "Customer" }; db.Customers.Add(customer);
        NotificationOutboxStaging.StagePasswordReset(db, customer, "protected-value", "https://client.example/reset?token=protected-value"); await db.SaveChangesAsync();
        var row = await db.Set<NotificationOutbox>().AsNoTracking().SingleAsync(); Assert.DoesNotContain("raw-secret", row.PayloadJson); Assert.Contains("recoveryUrl", row.PayloadJson); using var json = JsonDocument.Parse(row.PayloadJson); Assert.Equal("protected-value", json.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void DisabledTransportsNeverAdvertiseEnabled()
    {
        var options = Options.Create(new NotificationOptions { EmailEnabled = false, SmsEnabled = false });
        Assert.False(new SmtpNotificationTransport(options).Enabled);
        Assert.False(new TwilioSmsNotificationTransport(new StubHttpClientFactory(), options).Enabled);
    }

    [Fact]
    public void Mailtrap_transport_requires_sandbox_host_and_test_sender()
    {
        var valid = Options.Create(new NotificationOptions { EmailEnabled = true, Smtp = new SmtpOptions { Host = "sandbox.smtp.mailtrap.io", Port = 587, EnableSsl = true, Username = "user", Password = "secret", From = "no-reply@hoasap.test" } });
        var wrongHost = Options.Create(new NotificationOptions { EmailEnabled = true, Smtp = new SmtpOptions { Host = "smtp.example.com", Port = 587, EnableSsl = true, Username = "user", Password = "secret", From = "no-reply@hoasap.test" } });
        var wrongSender = Options.Create(new NotificationOptions { EmailEnabled = true, Smtp = new SmtpOptions { Host = "sandbox.smtp.mailtrap.io", Port = 587, EnableSsl = true, Username = "user", Password = "secret", From = "notify@example.com" } });

        Assert.True(new SmtpNotificationTransport(valid).Enabled);
        Assert.False(new SmtpNotificationTransport(wrongHost).Enabled);
        Assert.False(new SmtpNotificationTransport(wrongSender).Enabled);
    }

    [Fact]
    public async Task DeliveryTimeoutBecomesUnknownAndIsNotRetried()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Db(connection); await db.Database.EnsureCreatedAsync();
        var row = new NotificationOutbox { Channel = NotificationChannel.Sms, Destination = "+84901234567", PayloadJson = "{}" };
        db.Add(row); await db.SaveChangesAsync();
        var transport = new ThrowingTransport(new OperationCanceledException("provider timeout"));
        await RunWorker(db, transport);
        await RunWorker(db, transport);
        var saved = await db.Set<NotificationOutbox>().AsNoTracking().SingleAsync();
        Assert.Equal(NotificationStatus.Unknown, saved.Status);
        Assert.Equal(1, transport.Calls);
        Assert.Contains("unknown", saved.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredProcessingLeaseIsUnknownAndNeverReclaimed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Db(connection); await db.Database.EnsureCreatedAsync();
        var row = new NotificationOutbox { Channel = NotificationChannel.Sms, Destination = "+84901234567", Status = NotificationStatus.Processing, LeasedUntilUtc = DateTime.UtcNow.AddMinutes(-1), LeaseOwner = "dead-worker" };
        db.Add(row); await db.SaveChangesAsync();
        var transport = new ThrowingTransport(null);
        await RunWorker(db, transport);
        await RunWorker(db, transport);
        var saved = await db.Set<NotificationOutbox>().AsNoTracking().SingleAsync();
        Assert.Equal(NotificationStatus.Unknown, saved.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task DefinitePreAcceptanceFailureCanRetryWithSameMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Db(connection); await db.Database.EnsureCreatedAsync();
        var row = new NotificationOutbox { Channel = NotificationChannel.Sms, Destination = "+84901234567" };
        db.Add(row); await db.SaveChangesAsync();
        var transport = new ThrowingTransport(new NotificationDeliveryException("definitely not accepted", false));
        await RunWorker(db, transport);
        Assert.Equal(NotificationStatus.Pending, (await db.Set<NotificationOutbox>().AsNoTracking().SingleAsync()).Status);
        await db.Set<NotificationOutbox>().ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        await RunWorker(db, transport);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(2, (await db.Set<NotificationOutbox>().AsNoTracking().SingleAsync()).Attempts);
    }

    [Fact]
    public async Task Twilio_test_transport_uses_basic_auth_fixed_synthetic_numbers_and_no_idempotency_claim()
    {
        var sid = "AC" + new string('a', 32);
        var handler = new RecordingHandler(201, "{\"sid\":\"SM123\",\"status\":\"queued\"}");
        var transport = new TwilioSmsNotificationTransport(new RecordingFactory(handler), Options.Create(new NotificationOptions
        {
            SmsEnabled = true,
            Sms = new SmsOptions { Mode = "Test", TestAccountSid = sid, TestAuthToken = "test-token" }
        }));
        var row = new NotificationOutbox { Destination = "+84901234567", PayloadJson = "{\"code\":\"QA\",\"status\":\"Confirmed\"}" };

        Assert.True(transport.Enabled);
        await transport.SendAsync(row, CancellationToken.None);

        Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{sid}/Messages.json", handler.Url);
        Assert.Equal("Basic", handler.AuthorizationScheme);
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{sid}:test-token")), handler.AuthorizationParameter);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Contains("To=%2B12025550123", handler.FormBody);
        Assert.Contains("From=%2B15005550006", handler.FormBody);
        Assert.DoesNotContain("+84901234567", handler.FormBody);
        Assert.Null(handler.IdempotencyKey);
    }

    [Fact]
    public async Task Twilio_test_transport_never_sends_password_reset_url_or_token()
    {
        var sid = "AC" + new string('a', 32);
        var handler = new RecordingHandler(201, "{\"sid\":\"SM123\",\"status\":\"queued\"}");
        var transport = new TwilioSmsNotificationTransport(new RecordingFactory(handler), Options.Create(new NotificationOptions
        {
            SmsEnabled = true,
            Sms = new SmsOptions { Mode = "Test", TestAccountSid = sid, TestAuthToken = "test-token" }
        }));
        var token = "synthetic-reset-token-123";
        var row = new NotificationOutbox { Destination = "+84901234567", PayloadJson = JsonSerializer.Serialize(new { token, recoveryUrl = "https://shop.example.test/reset?token=" + token }) };

        await transport.SendAsync(row, CancellationToken.None);

        Assert.DoesNotContain(token, handler.FormBody);
        Assert.DoesNotContain("shop.example.test", handler.FormBody);
        Assert.DoesNotContain("recoveryUrl", handler.FormBody);
        Assert.DoesNotContain("+84901234567", handler.FormBody);
    }

    [Fact]
    public async Task Twilio_test_rejection_is_definite_but_server_failure_is_unknown()
    {
        var options = Options.Create(new NotificationOptions { SmsEnabled = true, Sms = new SmsOptions { Mode = "Test", TestAccountSid = "AC" + new string('a', 32), TestAuthToken = "test-token" } });
        var row = new NotificationOutbox { Destination = "+84901234567", PayloadJson = "{\"code\":\"QA\",\"status\":\"Confirmed\"}" };
        var badRequest = new TwilioSmsNotificationTransport(new RecordingFactory(new RecordingHandler(400, "{}")), options);
        var serverError = new TwilioSmsNotificationTransport(new RecordingFactory(new RecordingHandler(503, "{}")), options);

        Assert.False((await Assert.ThrowsAsync<NotificationDeliveryException>(() => badRequest.SendAsync(row, CancellationToken.None))).IsAmbiguous);
        Assert.True((await Assert.ThrowsAsync<NotificationDeliveryException>(() => serverError.SendAsync(row, CancellationToken.None))).IsAmbiguous);
    }

    [Fact]
    public void Twilio_test_transport_is_disabled_for_live_mode_or_incomplete_credentials()
    {
        var live = Options.Create(new NotificationOptions { SmsEnabled = true, Sms = new SmsOptions { Mode = "Live", TestAccountSid = "AC" + new string('a', 32), TestAuthToken = "test-token" } });
        var incomplete = Options.Create(new NotificationOptions { SmsEnabled = true, Sms = new SmsOptions { Mode = "Test", TestAccountSid = "AC" + new string('a', 32) } });

        Assert.False(new TwilioSmsNotificationTransport(new StubHttpClientFactory(), live).Enabled);
        Assert.False(new TwilioSmsNotificationTransport(new StubHttpClientFactory(), incomplete).Enabled);
    }

    private sealed class RecordingHandler(int status, string body) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? ContentType { get; private set; }
        public string? FormBody { get; private set; }
        public string? IdempotencyKey { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri?.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            FormBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            IdempotencyKey = request.Headers.Contains("Idempotency-Key") ? request.Headers.GetValues("Idempotency-Key").Single() : null;
            return new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body) };
        }
    }
    private sealed class RecordingFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task RunWorker(HoaSapDbContext db, INotificationTransport transport)
    {
        // A separate context shares the externally-owned SQLite connection. Worker scope
        // disposal must not dispose the caller context or leave its tracked entity stale.
        using var provider = new ServiceCollection().AddScoped(_ => Db((SqliteConnection)db.Database.GetDbConnection())).BuildServiceProvider();
        var worker = new NotificationWorker(provider.GetRequiredService<IServiceScopeFactory>(), new[] { transport },
            Options.Create(new NotificationOptions { SmsEnabled = true, LeaseSeconds = 60 }), NullLogger<NotificationWorker>.Instance);
        var method = typeof(NotificationWorker).GetMethod("ProcessBatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(worker, new object[] { CancellationToken.None })!;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private sealed class ThrowingTransport(Exception? error) : INotificationTransport
    {
        public NotificationChannel Channel => NotificationChannel.Sms;
        public bool Enabled => true;
        public int Calls { get; private set; }
        public Task SendAsync(NotificationOutbox message, CancellationToken ct)
        {
            Calls++;
            if (error is not null) return Task.FromException(error);
            return Task.CompletedTask;
        }
    }
}
