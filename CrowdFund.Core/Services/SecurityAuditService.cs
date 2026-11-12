using System.Text.Json;
using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

/// <summary>
/// Writes immutable security/compliance audit rows.
/// Persistence is injected via callback to avoid Core→Infrastructure dependency.
/// </summary>
public class SecurityAuditService
{
    public SecurityAuditEvent Create(
        string action,
        string? actorUserId,
        string? actorEmail,
        string? actorRole,
        string? entityType,
        string? entityId,
        string? ip,
        string? userAgent,
        object? details = null,
        string result = "ok")
    {
        return new SecurityAuditEvent
        {
            Action = action,
            ActorUserId = actorUserId,
            ActorEmail = actorEmail,
            ActorRole = actorRole,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = ip,
            UserAgent = userAgent is { Length: > 400 } ? userAgent[..400] : userAgent,
            DetailsJson = details == null ? null : JsonSerializer.Serialize(details),
            Result = result,
            CreatedAt = DateTime.UtcNow
        };
    }
}
