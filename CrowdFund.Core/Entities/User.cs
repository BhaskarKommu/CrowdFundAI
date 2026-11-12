using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public class ApplicationUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;
    [Required, MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;
    [Required, MaxLength(50)]
    public string Role { get; set; } = "Donor"; // Creator, Donor, Admin, Auditor
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? ProfileImageUrl { get; set; }

    /// <summary>Alias for DisplayName — not a DB column.</summary>
    [NotMapped]
    public string UserName
    {
        get => DisplayName;
        set => DisplayName = value;
    }
}

public static class Roles
{
    public const string Creator = "Creator";
    public const string Donor = "Donor";
    public const string Admin = "Admin";
    public const string Auditor = "Auditor";
}
