using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Spike.Seeding;

/// <summary>
///     An administrator API user, so ../check-package.py can call the package's lookup API. Local values only.
/// </summary>
internal sealed class ApiUserSeeder(IRuntimeState runtimeState, IUserService userService, IBackOfficeUserClientCredentialsManager credentials)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public static readonly Guid Key = new("50000000-0000-0000-0000-000000000001");
    public const string ClientId = "umbraco-back-office-spike-check";
    public const string ClientSecret = "spike-local-check-client-secret";

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run || await userService.GetAsync(Key) is not null)
        {
            return;
        }

        var user = new UserCreateModel
        {
            Id = Key,
            Kind = UserKind.Api,
            Name = "Spike check",
            Email = "spike-check@example.invalid",
            UserName = "spike-check@example.invalid",
            UserGroupKeys = new HashSet<Guid> { Constants.Security.AdminGroupKey },
        };
        await userService.CreateAsync(Constants.Security.SuperUserKey, user, approveUser: true);
        await credentials.SaveAsync(Key, ClientId, ClientSecret);
    }
}
