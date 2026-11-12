namespace CrowdFund.Core.Services;

/// <summary>Request-scoped tenant for hard multi-tenant isolation.</summary>
public class TenantContext
{
    public Guid? TenantId { get; set; }
    public string? Slug { get; set; }
    public bool IsPlatformAdmin { get; set; }

    public void RequireTenant()
    {
        if (!TenantId.HasValue && !IsPlatformAdmin)
            throw new InvalidOperationException("Tenant context required.");
    }
}
