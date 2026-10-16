using Microsoft.Extensions.DependencyInjection;
using PKVault.Core;
using PKVault.Core.OpenApi;

public class CoreRouterTests
{
    public CoreRouterTests()
    {
        Program.Initialize();
    }

    [Fact]
    public void NoRoutesDuplicates()
    {
        var sp = BuildTestServiceProvider();
        var routes = new CoreRouter().Routes;
        var duplicates = routes
            .GroupBy(r => (r.HttpMethod, r.HttpTemplate))
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void AllRoutesResolvableWithDI()
    {
        var sp = BuildTestServiceProvider();
        var routes = new CoreRouter().Routes;

        foreach (var controllerType in routes.Select(r => r.MethodInfo.DeclaringType).Distinct())
            Assert.NotNull(sp.GetService(controllerType!));
    }

    [Fact]
    public void AllRoutesTypesResolvableForJSON()
    {
        var routes = new CoreRouter().Routes;

        foreach (var route in routes)
        {
            Type[] types = [
                .. route.Parameters.Select(p => OpenApiGenerator.UnwrapTaskType(p.Param.ParameterType)),
                OpenApiGenerator.UnwrapTaskType(route.MethodInfo.ReturnType),
            ];

            foreach (var type in types)
            {
                AssertIsTypeJSONParsable(type);
            }
        }
    }

    [Fact]
    public void GetStatusCode_ShouldMapExceptionsToHttpStatus()
    {
        Assert.Equal(404, CoreRouter.GetStatusCode(new KeyNotFoundException()));
        Assert.Equal(409, CoreRouter.GetStatusCode(new ConflictException(ConflictException.PENDING_DELIVERY, "pending")));
        Assert.Equal(403, CoreRouter.GetStatusCode(new InvalidOperationException()));
        Assert.Equal(400, CoreRouter.GetStatusCode(new ArgumentException()));
        Assert.Equal(500, CoreRouter.GetStatusCode(new Exception()));
    }

    [Fact]
    public void RemoteRoutes_ShouldBeExposed()
    {
        var routes = new CoreRouter().Routes
            .Where(r => r.ControllerName == "Remote")
            .Select(r => (r.HttpMethod, r.HttpTemplate))
            .ToHashSet();

        Assert.Contains(("GET", "api/remote/ping"), routes);
        Assert.Contains(("POST", "api/remote/session/open"), routes);
        Assert.Contains(("POST", "api/remote/session/close"), routes);
        Assert.Contains(("GET", "api/remote/transfers"), routes);
        Assert.Contains(("GET", "api/remote/transfers/{id}/download"), routes);
        Assert.Contains(("POST", "api/remote/transfers/{id}/ack"), routes);
        Assert.Contains(("POST", "api/remote/transfers/{id}/abort"), routes);
    }

    private static void AssertIsTypeJSONParsable(Type type)
    {
        var finalType = CoreRouter.GetFinalType(type);
        if (CoreRouter.IsTypeVoidLike(finalType)
            || finalType == typeof(CoreFile)
            || typeof(ICoreResponse).IsAssignableFrom(finalType)
        )
            return;

        ArgumentNullException.ThrowIfNull(RouteJsonContext.Default.GetTypeInfo(finalType), $"Missing RouteJsonContext TypeInfo for type : {finalType}");
    }

    private static IServiceProvider BuildTestServiceProvider()
    {
        var services = new ServiceCollection();
        Program.ConfigureServices(services);
        return services.BuildServiceProvider();
    }
}
