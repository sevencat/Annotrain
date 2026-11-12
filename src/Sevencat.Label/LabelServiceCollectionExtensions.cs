using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sevencat.Label;

/// <summary>标注业务层的注册入口。Core 的存储/格式/导入导出链路由 <c>AddAnnotrainCore</c> 负责。</summary>
public static class LabelServiceCollectionExtensions
{
    public static IServiceCollection AddAnnotrainLabel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<LabelDatasetImportService>();
        services.TryAddSingleton<LabelProjectBrowser>();

        return services;
    }
}
