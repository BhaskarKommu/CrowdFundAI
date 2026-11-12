using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Data;

/// <summary>
/// Ensures demo Identity users exist with known passwords (idempotent).
/// Also patches common AspNetUsers schema gaps on SQL Server (e.g. missing UserName).
/// </summary>
public static class DemoAccounts
{
    public static readonly (string Email, string Password, string Role, string Display)[] Accounts =
    {
        ("admin@crowdfundai.demo", "Admin123!", "Admin", "Demo Admin"),
        ("auditor@crowdfundai.demo", "Auditor123!", "Auditor", "Demo Auditor"),
        ("creator1@crowdfundai.demo", "Creator123!", "Creator", "Creator 1"),
        ("creator2@crowdfundai.demo", "Creator123!", "Creator", "Creator 2"),
        ("donor1@crowdfundai.demo", "Donor123!", "Donor", "Donor 1"),
        ("donor2@crowdfundai.demo", "Donor123!", "Donor", "Donor 2"),
        ("donor3@crowdfundai.demo", "Donor123!", "Donor", "Donor 3"),
    };

    public static async Task EnsureAsync(
        UserManager<IdentityUser> userMgr,
        RoleManager<IdentityRole> roleMgr,
        AppDbContext db,
        ILogger logger)
    {
        await EnsureIdentityColumnsAsync(db, logger);

        foreach (var role in new[] { "Creator", "Donor", "Admin", "Auditor" })
        {
            if (!await roleMgr.RoleExistsAsync(role))
                await roleMgr.CreateAsync(new IdentityRole(role));
        }

        var hasher = new PasswordHasher<IdentityUser>();

        foreach (var (email, password, role, display) in Accounts)
        {
            await UpsertUserAsync(userMgr, db, hasher, email, password, role, display, logger);
        }

        for (var i = 3; i <= 10; i++)
            await UpsertUserAsync(userMgr, db, hasher, $"creator{i}@crowdfundai.demo", "Creator123!", "Creator", $"Creator {i}", logger);

        for (var i = 4; i <= 25; i++)
            await UpsertUserAsync(userMgr, db, hasher, $"donor{i}@crowdfundai.demo", "Donor123!", "Donor", $"Donor {i}", logger);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AppUsers SaveChanges after demo ensure — continuing");
        }
    }

    static async Task UpsertUserAsync(
        UserManager<IdentityUser> userMgr,
        AppDbContext db,
        PasswordHasher<IdentityUser> hasher,
        string email,
        string password,
        string role,
        string display,
        ILogger logger)
    {
        try
        {
            var user = await userMgr.FindByEmailAsync(email);
            if (user == null)
            {
                user = new IdentityUser
                {
                    UserName = email,
                    Email = email,
                    NormalizedUserName = email.ToUpperInvariant(),
                    NormalizedEmail = email.ToUpperInvariant(),
                    EmailConfirmed = true,
                    LockoutEnabled = false,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                };
                var create = await userMgr.CreateAsync(user, password);
                if (!create.Succeeded)
                {
                    logger.LogError("Failed to create {Email}: {Errors}",
                        email, string.Join("; ", create.Errors.Select(e => e.Description)));
                    return;
                }
                logger.LogInformation("Created demo user {Email}", email);
            }
            else
            {
                // Direct hash update — avoids ResetPassword token path
                user.PasswordHash = hasher.HashPassword(user, password);
                user.EmailConfirmed = true;
                user.LockoutEnd = null;
                if (string.IsNullOrEmpty(user.UserName))
                    user.UserName = email;
                if (string.IsNullOrEmpty(user.NormalizedUserName))
                    user.NormalizedUserName = email.ToUpperInvariant();
                if (string.IsNullOrEmpty(user.SecurityStamp))
                    user.SecurityStamp = Guid.NewGuid().ToString("N");

                // Prefer raw SQL so we only touch columns that exist
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        UPDATE AspNetUsers SET
                            PasswordHash = {0},
                            EmailConfirmed = 1,
                            LockoutEnd = NULL,
                            SecurityStamp = COALESCE(NULLIF(SecurityStamp, ''), {1}),
                            UserName = COALESCE(NULLIF(UserName, ''), {2}),
                            NormalizedUserName = COALESCE(NULLIF(NormalizedUserName, ''), {3}),
                            NormalizedEmail = COALESCE(NULLIF(NormalizedEmail, ''), {3})
                        WHERE Id = {4}
                        """,
                        user.PasswordHash,
                        user.SecurityStamp,
                        email,
                        email.ToUpperInvariant(),
                        user.Id);
                }
                catch (Exception sqlEx)
                {
                    logger.LogWarning(sqlEx, "Raw password update failed for {Email}, trying UserManager.UpdateAsync", email);
                    var upd = await userMgr.UpdateAsync(user);
                    if (!upd.Succeeded)
                        logger.LogError("Update failed for {Email}: {Errors}",
                            email, string.Join("; ", upd.Errors.Select(e => e.Description)));
                }
            }

            if (!await userMgr.IsInRoleAsync(user, role))
                await userMgr.AddToRoleAsync(user, role);

            // Super-admin: Admin role also receives every other role so policies pass everywhere
            if (role == "Admin")
            {
                foreach (var r in new[] { "Creator", "Donor", "Auditor" })
                {
                    if (!await userMgr.IsInRoleAsync(user, r))
                        await userMgr.AddToRoleAsync(user, r);
                }
            }

            if (!await db.AppUsers.AnyAsync(u => u.Id == user.Id))
            {
                db.AppUsers.Add(new ApplicationUser
                {
                    Id = user.Id,
                    Email = email,
                    DisplayName = display,
                    Role = role,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Demo account ensure failed for {Email}", email);
        }
    }

    /// <summary>
    /// Adds standard Identity columns if they are missing on an older AspNetUsers table.
    /// </summary>
    static async Task EnsureIdentityColumnsAsync(AppDbContext db, ILogger logger)
    {
        // SQL Server only; no-op / ignore on other providers
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'AspNetUsers', N'U') IS NOT NULL
                BEGIN
                    IF COL_LENGTH('AspNetUsers', 'UserName') IS NULL
                        ALTER TABLE AspNetUsers ADD UserName NVARCHAR(256) NULL;
                    IF COL_LENGTH('AspNetUsers', 'NormalizedUserName') IS NULL
                        ALTER TABLE AspNetUsers ADD NormalizedUserName NVARCHAR(256) NULL;
                    IF COL_LENGTH('AspNetUsers', 'Email') IS NULL
                        ALTER TABLE AspNetUsers ADD Email NVARCHAR(256) NULL;
                    IF COL_LENGTH('AspNetUsers', 'NormalizedEmail') IS NULL
                        ALTER TABLE AspNetUsers ADD NormalizedEmail NVARCHAR(256) NULL;
                    IF COL_LENGTH('AspNetUsers', 'EmailConfirmed') IS NULL
                        ALTER TABLE AspNetUsers ADD EmailConfirmed BIT NOT NULL CONSTRAINT DF_AspNetUsers_EmailConfirmed DEFAULT 0;
                    IF COL_LENGTH('AspNetUsers', 'PasswordHash') IS NULL
                        ALTER TABLE AspNetUsers ADD PasswordHash NVARCHAR(MAX) NULL;
                    IF COL_LENGTH('AspNetUsers', 'SecurityStamp') IS NULL
                        ALTER TABLE AspNetUsers ADD SecurityStamp NVARCHAR(MAX) NULL;
                    IF COL_LENGTH('AspNetUsers', 'ConcurrencyStamp') IS NULL
                        ALTER TABLE AspNetUsers ADD ConcurrencyStamp NVARCHAR(MAX) NULL;
                    IF COL_LENGTH('AspNetUsers', 'PhoneNumber') IS NULL
                        ALTER TABLE AspNetUsers ADD PhoneNumber NVARCHAR(MAX) NULL;
                    IF COL_LENGTH('AspNetUsers', 'PhoneNumberConfirmed') IS NULL
                        ALTER TABLE AspNetUsers ADD PhoneNumberConfirmed BIT NOT NULL CONSTRAINT DF_AspNetUsers_PhoneNumberConfirmed DEFAULT 0;
                    IF COL_LENGTH('AspNetUsers', 'TwoFactorEnabled') IS NULL
                        ALTER TABLE AspNetUsers ADD TwoFactorEnabled BIT NOT NULL CONSTRAINT DF_AspNetUsers_TwoFactorEnabled DEFAULT 0;
                    IF COL_LENGTH('AspNetUsers', 'LockoutEnd') IS NULL
                        ALTER TABLE AspNetUsers ADD LockoutEnd DATETIMEOFFSET NULL;
                    IF COL_LENGTH('AspNetUsers', 'LockoutEnabled') IS NULL
                        ALTER TABLE AspNetUsers ADD LockoutEnabled BIT NOT NULL CONSTRAINT DF_AspNetUsers_LockoutEnabled DEFAULT 0;
                    IF COL_LENGTH('AspNetUsers', 'AccessFailedCount') IS NULL
                        ALTER TABLE AspNetUsers ADD AccessFailedCount INT NOT NULL CONSTRAINT DF_AspNetUsers_AccessFailedCount DEFAULT 0;

                    -- Backfill UserName from Email where empty
                    UPDATE AspNetUsers SET UserName = Email WHERE UserName IS NULL AND Email IS NOT NULL;
                    UPDATE AspNetUsers SET NormalizedUserName = UPPER(UserName) WHERE NormalizedUserName IS NULL AND UserName IS NOT NULL;
                    UPDATE AspNetUsers SET NormalizedEmail = UPPER(Email) WHERE NormalizedEmail IS NULL AND Email IS NOT NULL;
                END
                """);
            logger.LogInformation("AspNetUsers schema check completed.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not patch AspNetUsers schema (non-SQL Server or permissions).");
        }
    }
}
