using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DocSearch.Api.Tests.Security;

public sealed class RouteAuthorizationReflectionTests
{
    private static readonly HashSet<string> _allowedAnonymousEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "api/v{version:apiVersion}/auth/register:POST",
        "api/v{version:apiVersion}/auth/login:POST"
    };

    [Fact]
    public void All_Controller_Endpoints_Enforce_Authorization_Except_Explicit_AllowList()
    {
        var apiAssembly = typeof(DocSearch.Api.Controllers.AuthController).Assembly;
        var controllerTypes = apiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(controllerTypes);

        var accidentallyAnonymous = new List<string>();

        foreach (var controller in controllerTypes)
        {
            var controllerRoute = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            var controllerAuthorize = controller.GetCustomAttribute<AuthorizeAttribute>();
            var controllerAllowAnon = controller.GetCustomAttribute<AllowAnonymousAttribute>();

            var actionMethods = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && !m.GetCustomAttributes<NonActionAttribute>().Any());

            foreach (var action in actionMethods)
            {
                var actionRoute = action.GetCustomAttribute<RouteAttribute>()?.Template;
                var httpMethodAttr = action.GetCustomAttributes().OfType<HttpMethodAttribute>().FirstOrDefault();
                var httpMethod = httpMethodAttr?.HttpMethods.FirstOrDefault() ?? "ANY";

                var actionTemplate = actionRoute ?? httpMethodAttr?.Template ?? string.Empty;
                var combinedRoute = string.IsNullOrEmpty(actionTemplate)
                    ? controllerRoute
                    : $"{controllerRoute.TrimEnd('/')}/{actionTemplate.TrimStart('/')}";

                var routeKey = $"{combinedRoute.Trim('/')}:{httpMethod.ToUpperInvariant()}";

                var actionAuthorize = action.GetCustomAttribute<AuthorizeAttribute>();
                var actionAllowAnon = action.GetCustomAttribute<AllowAnonymousAttribute>();

                var isAnonymous = actionAllowAnon != null || (controllerAllowAnon != null && actionAuthorize == null);

                if (isAnonymous)
                {
                    if (!_allowedAnonymousEndpoints.Contains(routeKey))
                    {
                        accidentallyAnonymous.Add($"Controller: {controller.Name}, Action: {action.Name}, Route: {routeKey} is unexpectedly anonymous.");
                    }
                }
                else
                {
                    var hasAuthorize = controllerAuthorize != null || actionAuthorize != null;
                    if (!hasAuthorize)
                    {
                        accidentallyAnonymous.Add($"Controller: {controller.Name}, Action: {action.Name}, Route: {routeKey} lacks [Authorize].");
                    }
                }
            }
        }

        Assert.Empty(accidentallyAnonymous);
    }
}
