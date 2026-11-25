using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Identity.Domain;

namespace PayFlow.Modules.Identity.Infrastructure;

internal sealed class IdentityDbModel : IModuleDbModel
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MerchantMembership>(e =>
        {
            var roles = string.Join(", ", MerchantRoles.All.Select(r => $"'{r}'"));
            e.ToTable("merchant_memberships", PayFlowDbContext.IdentitySchema, t =>
                t.HasCheckConstraint("ck_merchant_memberships_role", $"role IN ({roles})"));
            e.HasKey(m => new { m.UserId, m.MerchantId });
            e.Property(m => m.Role).HasMaxLength(30);
            e.HasIndex(m => m.MerchantId);
            e.HasOne<PayFlowUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserMfa>(e =>
        {
            e.ToTable("user_mfa", PayFlowDbContext.IdentitySchema);
            e.HasKey(m => m.UserId);
            e.Property(m => m.SecretCiphertext).HasMaxLength(1000);
            e.Property(m => m.PendingSecretCiphertext).HasMaxLength(1000);
            e.HasOne<PayFlowUser>().WithOne().HasForeignKey<UserMfa>(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MfaRecoveryCode>(e =>
        {
            e.ToTable("mfa_recovery_codes", PayFlowDbContext.IdentitySchema);
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.UserId);
            e.HasOne<PayFlowUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
