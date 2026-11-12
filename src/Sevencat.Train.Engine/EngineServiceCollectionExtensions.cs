using Microsoft.Extensions.DependencyInjection;
using Sevencat.Core.Simulation;
using Sevencat.Train.Engine.Runners;

namespace Sevencat.Train.Engine;

/// <summary>训练引擎的 DI 注册。</summary>
public static class EngineServiceCollectionExtensions
{
    /// <summary>
    /// 把 <see cref="IDatasetTrainingRunner"/> 换成真训练引擎。
    /// <para>必须在 <c>AddAnnotrainCore</c> 之后调用（容器按最后注册解析单实例）。</para>
    /// </summary>
    public static IServiceCollection AddAnnotrainTrainingEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDatasetTrainingRunner, YoloDatasetTrainingRunner>();
        return services;
    }
}
