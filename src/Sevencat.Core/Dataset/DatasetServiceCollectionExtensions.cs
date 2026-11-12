using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sevencat.Core.Annotation;
using Sevencat.Core.Annotation.Formats;
using Sevencat.Core.Dataset.Export;
using Sevencat.Core.Dataset.Import;
using Sevencat.Core.Imaging;
using Sevencat.Core.Simulation;
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

        // codec 与路径规则放在 Core：导出 YOLO / RF-DETR 与导入都走同一批实现，不在各端重复注册
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnnotationCodec, YoloTxtCodec>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnnotationCodec, LabelmeCodec>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnnotationPathRule, SiblingAnnotationPathRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnnotationPathRule, YoloAnnotationPathRule>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatasetStoreProvider, YoloDatasetStoreProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatasetStoreProvider, CocoDatasetStoreProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatasetStoreProvider, TinyLabel.TinyLabelDatasetStoreProvider>());
        services.TryAddSingleton<TinyLabel.TinyLabelProjectCatalog>();
        services.TryAddSingleton<TinyLabel.TinyLabelProjectService>();

        services.TryAddSingleton<DatasetStoreResolver>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatasetExporter, YoloDatasetExporter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatasetExporter, CocoDatasetExporter>());
        services.TryAddSingleton<DatasetExportService>();

        // 反向链路：现有 YOLO / RF-DETR 数据集 → 标注项目库（图片 + 标注 + train/val）
        services.TryAddSingleton<DatasetImportService>();

        // 训练入口：现在是干跑（完整装载 + 校验 + 摘要），M4 接真训练引擎时只换这一行
        services.TryAddSingleton<IDatasetTrainingRunner, SummaryTrainingRunner>();
        services.TryAddSingleton<SimulationPipeline>();

        return services;
    }
}
