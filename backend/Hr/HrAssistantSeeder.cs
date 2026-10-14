// মানব সম্পদ — the hr-assistant account (docs/HR_MODULE_PROMPT.md §3.1).
//
// One account named "hr-assistant" with the HrAssistant role, made once: only
// when no user of that name exists, so a restart never makes a second one and
// never touches the first. The secret comes from the environment variable
// HR_ASSISTANT_SECRET and nowhere else; with no variable the step is skipped
// with one log line. More HR staff get their own accounts with the same role
// through the Access screen.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Rbac;
using RabbitIC.Api.Models;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Hr;

public static class HrAssistantSeeder
{
    public const string Username = "hr-assistant";
    public const string SecretVariable = "HR_ASSISTANT_SECRET";

    public static async Task RunAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var secret = config[SecretVariable];
        if (string.IsNullOrWhiteSpace(secret))
        {
            logger.LogWarning("HR: {Variable} is not set, so the {Username} account was not created.", SecretVariable, Username);
            return;
        }

        var db = services.GetRequiredService<AppDbContext>();
        if (await db.Users.AnyAsync(u => u.Username == Username)) return;

        // The account itself is made exactly as /auth/login makes one, so the
        // secret verifies through the same code path as every other login.
        var (hash, salt) = Authorization.HashSecret(secret);
        var user = new User
        {
            Username = Username,
            SecretHash = hash,
            SecretSalt = salt,
            Token = Authorization.NewToken(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // The role goes through the admin API's own service, acting as the seeded
        // administrator, so this step obeys the same rules as the Access screen.
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Role == UserRoles.SystemAdmin);
        var rbac = services.GetRequiredService<RbacService>();
        var result = admin is null
            ? null
            : await rbac.SetRoleAsync(admin, user.Id, new SetRoleInput(UserRoles.HrAssistant));

        if (result?.Outcome != RbacOutcome.Ok)
        {
            logger.LogWarning("HR: created {Username} but could not make it {Role}: {Error}",
                Username, UserRoles.HrAssistant, result?.Error ?? "no SystemAdmin to act as.");
            return;
        }

        // The username and role only — never the secret.
        logger.LogInformation("HR: created {Username} as {Role}.", Username, UserRoles.HrAssistant);
    }
}
