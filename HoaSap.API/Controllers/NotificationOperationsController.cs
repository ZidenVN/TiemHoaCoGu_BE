using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using HoaSap.DAL;
using HoaSap.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HoaSap.API.Controllers;

[ApiController, Authorize(Roles = "Admin"), Route("api/management/notifications")]
public sealed class NotificationOperationsController(HoaSapDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationOutboxPage>> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100 || page > int.MaxValue / pageSize)
            return BadRequest("page/pageSize is out of bounds");

        var query = db.Set<NotificationOutbox>().AsNoTracking()
            .Where(x => x.Status == NotificationStatus.Unknown);
        var total = await query.LongCountAsync(ct);
        var skip = checked((page - 1) * pageSize);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip(skip).Take(pageSize)
            .Select(x => new NotificationOutboxListItem(
                x.Id, x.EventType, x.Channel, x.Status, x.Attempts, x.CreatedAtUtc, x.NextAttemptAtUtc))
            .ToListAsync(ct);
        return Ok(new NotificationOutboxPage(items, total, page, pageSize));
    }

    [HttpPost("{id:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(Guid id, NotificationReconciliationRequest request, CancellationToken ct)
    {
        var outcome = request.Outcome.Trim().ToLowerInvariant();
        if (outcome is not ("accepted" or "not-accepted"))
            return BadRequest("Outcome must be 'accepted' or 'not-accepted'.");

        var evidenceReference = request.EvidenceReference.Trim();
        if (string.IsNullOrWhiteSpace(evidenceReference))
            return BadRequest("A provider evidence reference is required.");

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            return Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var message = await db.Set<NotificationOutbox>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (message is null) return NotFound();
        if (message.Status != NotificationStatus.Unknown)
            return Conflict("Only notifications with an Unknown delivery outcome can be reconciled.");

        var previousStatus = message.Status;
        var now = DateTime.UtcNow;
        var accepted = outcome == "accepted";
        var newStatus = accepted ? NotificationStatus.Sent : NotificationStatus.Pending;
        message.Status = newStatus;
        message.SentAtUtc = accepted ? now : null;
        message.NextAttemptAtUtc = accepted ? message.NextAttemptAtUtc : now;
        message.LeasedUntilUtc = null;
        message.LeaseOwner = null;
        message.LastError = accepted
            ? null
            : "Admin verified the provider did not accept this message; queued for a controlled retry.";

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(NotificationOutbox),
            EntityId = message.Id.ToString(),
            Action = "Reconcile",
            OldValuesJson = JsonSerializer.Serialize(new { status = previousStatus.ToString() }),
            NewValuesJson = JsonSerializer.Serialize(new
            {
                status = newStatus.ToString(),
                outcome,
                evidenceReference
            }),
            OccurredAtUtc = now,
            ActorId = actorId,
            ActorRole = User.FindFirstValue(ClaimTypes.Role),
            TraceId = HttpContext.TraceIdentifier
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(new { id = message.Id, status = message.Status.ToString() });
    }
}

public sealed record NotificationOutboxListItem(
    Guid Id,
    string EventType,
    NotificationChannel Channel,
    NotificationStatus Status,
    int Attempts,
    DateTime CreatedAtUtc,
    DateTime NextAttemptAtUtc);

public sealed record NotificationOutboxPage(
    IReadOnlyList<NotificationOutboxListItem> Items,
    long Total,
    int Page,
    int PageSize);

public sealed class NotificationReconciliationRequest
{
    [Required, StringLength(24, MinimumLength = 5)]
    public string Outcome { get; set; } = "";

    [Required, StringLength(120, MinimumLength = 5),
     RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:/# -]{3,118}[A-Za-z0-9]$")]
    public string EvidenceReference { get; set; } = "";
}
