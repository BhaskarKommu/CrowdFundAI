using CrowdFund.Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<IdentityUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ApplicationUser> AppUsers => Set<ApplicationUser>();
    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();
    public DbSet<SavedPaymentMethod> SavedPaymentMethods => Set<SavedPaymentMethod>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoicePayment> InvoicePayments => Set<InvoicePayment>();
    public DbSet<PaymentLink> PaymentLinks => Set<PaymentLink>();
    public DbSet<PaymentLinkTransaction> PaymentLinkTransactions => Set<PaymentLinkTransaction>();
    public DbSet<PaymentRuntimeSettings> PaymentRuntimeSettings => Set<PaymentRuntimeSettings>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignDocument> CampaignDocuments => Set<CampaignDocument>();
    public DbSet<VerificationRun> VerificationRuns => Set<VerificationRun>();
    public DbSet<AgentFinding> AgentFindings => Set<AgentFinding>();
    public DbSet<Donation> Donations => Set<Donation>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseEvidence> ExpenseEvidences => Set<ExpenseEvidence>();
    public DbSet<ExpenseLineItem> ExpenseLineItems => Set<ExpenseLineItem>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<CampaignUpdate> CampaignUpdates => Set<CampaignUpdate>();
    public DbSet<FraudFlag> FraudFlags => Set<FraudFlag>();
    public DbSet<AuditLedgerEntry> AuditLedger => Set<AuditLedgerEntry>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<EmbeddingRecord> Embeddings => Set<EmbeddingRecord>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<CreatorProfile> CreatorProfiles => Set<CreatorProfile>();
    public DbSet<KycSubmission> KycSubmissions => Set<KycSubmission>();
    public DbSet<KycDocument> KycDocuments => Set<KycDocument>();
    public DbSet<AmlScreening> AmlScreenings => Set<AmlScreening>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<DonationTaxInfo> DonationTaxInfos => Set<DonationTaxInfo>();
    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();
    public DbSet<TransparencyScoreSnapshot> TransparencyScoreSnapshots => Set<TransparencyScoreSnapshot>();
    public DbSet<ReceiptChallenge> ReceiptChallenges => Set<ReceiptChallenge>();
    public DbSet<RiskGraphEdge> RiskGraphEdges => Set<RiskGraphEdge>();
    public DbSet<FlagReply> FlagReplies => Set<FlagReply>();
    public DbSet<TransparencyLogEntry> TransparencyLog => Set<TransparencyLogEntry>();
    public DbSet<ReverificationRun> ReverificationRuns => Set<ReverificationRun>();
    public DbSet<CampaignMatchLog> CampaignMatchLogs => Set<CampaignMatchLog>();
    public DbSet<ImpactReport> ImpactReports => Set<ImpactReport>();
    public DbSet<MultimodalProof> MultimodalProofs => Set<MultimodalProof>();
    public DbSet<MilestoneRelease> MilestoneReleases => Set<MilestoneRelease>();
    public DbSet<RecurringDonation> RecurringDonations => Set<RecurringDonation>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<SubscriptionBillingEvent> SubscriptionBillingEvents => Set<SubscriptionBillingEvent>();
    public DbSet<MatchingGiftProgram> MatchingGiftPrograms => Set<MatchingGiftProgram>();
    public DbSet<MatchingGiftLedgerEntry> MatchingGiftLedger => Set<MatchingGiftLedgerEntry>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<RefundRequest> RefundRequests => Set<RefundRequest>();
    public DbSet<RefundTransaction> RefundTransactions => Set<RefundTransaction>();
    public DbSet<DisputeCase> DisputeCases => Set<DisputeCase>();
    public DbSet<FxRateSnapshot> FxRates => Set<FxRateSnapshot>();
    public DbSet<DonorPreference> DonorPreferences => Set<DonorPreference>();
    public DbSet<CreatorMessageBlast> CreatorMessageBlasts => Set<CreatorMessageBlast>();
    public DbSet<ShortLink> ShortLinks => Set<ShortLink>();
    public DbSet<ImpactCertificate> ImpactCertificates => Set<ImpactCertificate>();
    public DbSet<PushNotificationLog> PushNotificationLogs => Set<PushNotificationLog>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<PartnerClient> PartnerClients => Set<PartnerClient>();
    public DbSet<PartnerAccessToken> PartnerAccessTokens => Set<PartnerAccessToken>();
    public DbSet<OutboundWebhook> OutboundWebhooks => Set<OutboundWebhook>();
    public DbSet<PremiumVerificationSubscription> PremiumVerificationSubscriptions => Set<PremiumVerificationSubscription>();
    public DbSet<EnterpriseAccount> EnterpriseAccounts => Set<EnterpriseAccount>();
    public DbSet<PortfolioTransparencyReport> PortfolioTransparencyReports => Set<PortfolioTransparencyReport>();
    public DbSet<EnterpriseApiUsage> EnterpriseApiUsage => Set<EnterpriseApiUsage>();
    public DbSet<CompliancePack> CompliancePacks => Set<CompliancePack>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<SecurityAuditEvent>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Action, x.CreatedAt });
            e.HasIndex(x => x.ActorUserId);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
        });

        builder.Entity<Donation>()
            .HasIndex(d => d.PayPalCaptureId)
            .IsUnique()
            .HasFilter("[PayPalCaptureId] IS NOT NULL");

        builder.Entity<AuditLedgerEntry>()
            .HasIndex(e => new { e.CampaignId, e.Id });

        builder.Entity<Expense>()
            .HasIndex(e => new { e.CampaignId, e.Date });

        builder.Entity<WebhookEvent>()
            .HasIndex(w => w.PayPalEventId)
            .IsUnique();

        builder.Entity<Campaign>()
            .HasOne(c => c.Creator)
            .WithMany()
            .HasForeignKey(c => c.CreatorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Donation>()
            .HasOne(d => d.Campaign)
            .WithMany(c => c.Donations)
            .HasForeignKey(d => d.CampaignId);

        builder.Entity<Expense>()
            .HasOne(e => e.Campaign)
            .WithMany(c => c.Expenses)
            .HasForeignKey(e => e.CampaignId);
    }
}
