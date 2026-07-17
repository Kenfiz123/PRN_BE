using Hangfire.Dashboard;
using ClubReportHub.Shared.Auth;

namespace ReportService.Jobs;

public sealed class AllowAllDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var user = httpContext.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return user.IsInRole(AuthRoles.Admin) || user.IsInRole(AuthRoles.SystemAdmin);
    }
}
