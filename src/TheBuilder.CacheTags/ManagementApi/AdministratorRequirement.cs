using Microsoft.AspNetCore.Authorization;
using Umbraco.Cms.Core.Security;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.ManagementApi;

internal sealed class AdministratorRequirement : IAuthorizationRequirement
{
    public const string Policy = "TheBuilder.CacheTags.Administrator";
}

internal sealed class AdministratorHandler(IBackOfficeSecurityAccessor securityAccessor)
    : AuthorizationHandler<AdministratorRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdministratorRequirement requirement)
    {
        if (context.User.IsInRole(Umbraco.Cms.Core.Constants.Security.AdminGroupAlias)
            || securityAccessor.BackOfficeSecurity?.CurrentUser?.IsAdmin() is true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
