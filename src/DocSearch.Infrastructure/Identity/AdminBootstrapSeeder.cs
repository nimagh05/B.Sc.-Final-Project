using DocSearch.Application.Options;
using DocSearch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Identity;

public static class AdminBootstrapSeeder
{
    public const string AdminRole = "Admin";
    public const string UserRole = "User";

    public static async Task SeedAdminAsync(IServiceProvider serviceProvider, CancellationToken ct = default)
    {
        using var scope = serviceProvider.CreateScope();
        var logger = scope.ServiceProvider.GetService<ILogger<AppDbContext>>();

        try
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await dbContext.Database.CanConnectAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var authOptions = scope.ServiceProvider.GetService<IOptions<AuthOptions>>()?.Value;

            // 1. Ensure system roles exist
            if (!await roleManager.RoleExistsAsync(AdminRole).ConfigureAwait(false))
            {
                await roleManager.CreateAsync(new IdentityRole(AdminRole)).ConfigureAwait(false);
            }

            if (!await roleManager.RoleExistsAsync(UserRole).ConfigureAwait(false))
            {
                await roleManager.CreateAsync(new IdentityRole(UserRole)).ConfigureAwait(false);
            }

            // 2. Identify configured bootstrap admin credentials
            var adminEmail = authOptions?.BootstrapAdmin?.Email
                ?? configuration["Auth:AdminEmail"]
                ?? configuration["Auth:BootstrapAdmin:Email"];

            var adminPassword = authOptions?.BootstrapAdmin?.Password
                ?? configuration["Auth:AdminPassword"]
                ?? configuration["Auth:BootstrapAdmin:Password"];

            if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            {
                return;
            }

            // 3. Create or promote admin user idempotently
            var adminUser = await userManager.FindByEmailAsync(adminEmail).ConfigureAwait(false);
            if (adminUser == null)
            {
                adminUser = new ApplicationUser
                {
                    Id = Guid.NewGuid().ToString(),
                    UserName = adminEmail,
                    Email = adminEmail,
                    EmailConfirmed = true
                };

                var createResult = await userManager.CreateAsync(adminUser, adminPassword).ConfigureAwait(false);
                if (!createResult.Succeeded)
                {
                    var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));
                    logger?.LogWarning("Failed to bootstrap admin user {Email}: {Errors}", adminEmail, errors);
                    return;
                }
            }

            if (!await userManager.IsInRoleAsync(adminUser, AdminRole).ConfigureAwait(false))
            {
                await userManager.AddToRoleAsync(adminUser, AdminRole).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not complete admin bootstrap seeding. Ensure database is accessible.");
        }
    }
}
