using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum EnterpriseTier
{
    Standard = 0,
    Professional = 1,
    Enterprise = 2
}

public enum PortfolioReportStatus
{
    Draft = 0,
    Generating = 1,
    Ready = 2,
    Failed = 3,
    Sent = 4
}

/// <summary>Enterprise subscription binding a Tenant to Transparency-as-a-Service entitlements.</summary>
public class EnterpriseAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public EnterpriseTier Tier { get; set; } = EnterpriseTier.Enterprise;
    public bool IsActive { get; set; } = true;

    public int MaxCampaigns { get; set; } = 500;
    public int MaxApiCallsPerDay { get; set; } = 50_000;
    public bool ScheduledReportsEnabled { get; set; } = true;
    public bool BulkVerifyEnabled { get; set; } = true;
    public bool ComplianceExportEnabled { get; set; } = true;
    public bool SsoPlaceholderEnabled { get; set; } = true;

    /// <summary>Cron-like: daily | weekly | monthly</summary>
    [MaxLength(20)]
    public string ReportCadence { get; set; } = "weekly";
    [MaxLength(500)]
    public string? ReportRecipients { get; set; } // comma emails

    [MaxLength(450)]
    public string? PrimaryContactUserId { get; set; }
    public DateTime? ContractStartsAt { get; set; }
    public DateTime? ContractEndsAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PortfolioTransparencyReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? EnterpriseAccountId { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = "Portfolio transparency report";
    public PortfolioReportStatus Status { get; set; } = PortfolioReportStatus.Draft;

    public int CampaignCount { get; set; }
    public decimal TotalRaised { get; set; }
    public decimal TotalVerifiedSpend { get; set; }
    public double AvgTrustScore { get; set; }
    public double AvgTransparencyScore { get; set; }
    public int OpenHighFlags { get; set; }

    public string SummaryMarkdown { get; set; } = "";
    public string? MetricsJson { get; set; }
    public string? HtmlBody { get; set; }

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class EnterpriseApiUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateTime Day { get; set; } // UTC date
    public int CallCount { get; set; }
}

public class CompliancePack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CampaignId { get; set; }
    [MaxLength(40)]
    public string PackType { get; set; } = "portfolio"; // portfolio | campaign
    public string ManifestJson { get; set; } = "{}";
    public string? DownloadHint { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
