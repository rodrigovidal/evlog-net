using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace Evlog;

public static class EvlogServiceExtensions
{
    public static IServiceCollection AddEvlog(
        this IServiceCollection services,
        Action<EvlogOptions> configure)
    {
        services.Configure(configure);

        services.TryAddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        services.TryAddSingleton(sp =>
        {
            var provider = sp.GetRequiredService<ObjectPoolProvider>();
            return provider.Create(new DefaultPooledObjectPolicy<RequestLogger>());
        });

        services.AddHttpContextAccessor();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, EvlogLoggerProvider>());

        return services;
    }

    public static IApplicationBuilder UseEvlog(this IApplicationBuilder app)
    {
        return app.UseMiddleware<EvlogMiddleware>();
    }
}
