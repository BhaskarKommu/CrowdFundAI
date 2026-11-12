using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

/// <summary>Platform customer profile linked to Identity user (and optional guest key).</summary>
public class CustomerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ASP.NET Identity user id when registered.</summary>
    [MaxLength(450)]
    public string? UserId { get; set; }

    [MaxLength(256)]
    public string? Email { get; set; }
    [MaxLength(120)]
    public string? DisplayName { get; set; }
    [MaxLength(32)]
    public string? Phone { get; set; }

    /// <summary>PayPal customer / payer id when known from vault or capture.</summary>
    [MaxLength(64)]
    public string? PayPalPayerId { get; set; }
    [MaxLength(64)]
    public string? PayPalCustomerId { get; set; }

    // Billing
    [MaxLength(200)]
    public string? BillingName { get; set; }
    [MaxLength(200)]
    public string? BillingLine1 { get; set; }
    [MaxLength(200)]
    public string? BillingLine2 { get; set; }
    [MaxLength(100)]
    public string? BillingCity { get; set; }
    [MaxLength(100)]
    public string? BillingRegion { get; set; }
    [MaxLength(20)]
    public string? BillingPostalCode { get; set; }
    [MaxLength(2)]
    public string? BillingCountry { get; set; }

    public bool MarketingOptIn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<SavedPaymentMethod> SavedPaymentMethods { get; set; } = new List<SavedPaymentMethod>();
}

/// <summary>
/// Tokenized saved funding source only.
/// NEVER store PAN, CVV/CVC, full account numbers, magnetic track data, or PIN.
/// Only PayPal vault/setup token ids and non-sensitive display metadata (brand, last4).
/// </summary>
public class SavedPaymentMethod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerProfileId { get; set; }
    public CustomerProfile? Customer { get; set; }

    /// <summary>paypal | card (display type only — card data lives at PayPal).</summary>
    [MaxLength(20)]
    public string Type { get; set; } = "paypal";
    /// <summary>PayPal vault payment token id (opaque).</summary>
    [MaxLength(100)]
    public string? PayPalVaultId { get; set; }
    /// <summary>PayPal setup-token id used during vault approval (opaque).</summary>
    [MaxLength(100)]
    public string? SetupTokenId { get; set; }
    [MaxLength(40)]
    public string? Brand { get; set; }
    /// <summary>Last 4 digits for UI only — never full card number.</summary>
    [MaxLength(4)]
    public string? Last4 { get; set; }
    [MaxLength(20)]
    public string Status { get; set; } = "Active";
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RemovedAt { get; set; }
}
