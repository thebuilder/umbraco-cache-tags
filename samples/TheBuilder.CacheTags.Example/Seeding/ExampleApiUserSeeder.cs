using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Security.OperationStatus;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace TheBuilder.CacheTags.Example.Seeding;

/// <summary>
///     The API user ../nextjs/scripts/e2e.mts signs in as to publish changes through the Management API.
///     Local values only; the same ones are in ../nextjs/.env.
/// </summary>
public static class ExampleApiUser
{
    public static readonly Guid Key = new("e4000000-0000-0000-0000-000000000001");

    /// <summary>Umbraco only accepts client ids that start with <c>umbraco-back-office-</c>.</summary>
    public const string ClientId = "umbraco-back-office-cache-tags-e2e";

    public const string ClientSecret = "example-local-e2e-client-secret";
}

/// <summary>
///     Creates the <see cref="ExampleApiUser" /> on start, in the Administrators group, unless it exists.
///     Checked on its own, so a database seeded before this user was added gets it too.
/// </summary>
internal sealed class ExampleApiUserSeeder(
    IRuntimeState runtimeState,
    IUserService userService,
    IBackOfficeUserClientCredentialsManager clientCredentialsManager,
    ILogger<ExampleApiUserSeeder> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run || await userService.GetAsync(ExampleApiUser.Key) is not null)
        {
            return;
        }

        logger.LogInformation("Creating the end-to-end check's API user.");

        var user = new UserCreateModel
        {
            Id = ExampleApiUser.Key,
            Kind = UserKind.Api,
            Name = "End-to-end check",
            Email = "e2e@example.invalid",
            UserName = "e2e@example.invalid",
            UserGroupKeys = new HashSet<Guid> { Constants.Security.AdminGroupKey },
        };
        Attempt<UserCreationResult, UserOperationStatus> created =
            await userService.CreateAsync(Constants.Security.SuperUserKey, user, approveUser: true);
        if (created.Success is false)
        {
            throw new InvalidOperationException($"Creating the API user failed: {created.Status}");
        }

        Attempt<BackOfficeUserClientCredentialsOperationStatus> credentials =
            await clientCredentialsManager.SaveAsync(ExampleApiUser.Key, ExampleApiUser.ClientId, ExampleApiUser.ClientSecret);
        if (credentials.Success is false)
        {
            throw new InvalidOperationException($"Adding client credentials to the API user failed: {credentials.Result}");
        }
    }
}
