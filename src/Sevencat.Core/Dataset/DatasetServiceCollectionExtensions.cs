using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sevencat.Core.Annotation;
using Sevencat.Core.Annotation.Formats;
using Sevencat.Core.Imaging;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset;

public static class DatasetServiceCollectionExtensions
{
    /// <summary>
    /// 注册数据集链路：存储后端工厂与注册表、图像信息源、格式探测、各形态 Provider、统一打开入口。
    /// 形态 Provider 之间互不依赖，新增形态只需在这里加一行 <c>TryAddEnumerable</c>。
    /// </summary>
    public static IServiceCollection AddAnnotrainDatasets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IBlobStoreFactory, LocalBlobStoreFactory>();
        services.TryAddSingleton<BlobStoreRegistry>();

        services.TryAddSingleton<IImageInfoSource>(provider =>
            new BlobImageInfoSource(provider.GetRequiredService<BlobStoreRegistry>()));

        services.TryAddSingleton<IDatasetProbe, DatasetProbeService>();

        // codec 与路径规则放在 Core：标注端写标注、训练端读平铺数据集都用同一批，避免各自注册导致「读到无框样本」
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnnotationCodec, YoloTxtCodec>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnnotationCodec, LabelmeCodec>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnnotationPathRule, SiblingAnnotationPathRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnnotationPathRule, YoloAnnotationPathRule>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatasetStoreProvider, YoloDatasetStoreProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatasetStoreProvider, CocoDatasetStoreProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatasetStoreProvider, FlatDatasetStoreProvider>());

        services.TryAddSingleton<DatasetStoreResolver>();

        return services;
    }
}
