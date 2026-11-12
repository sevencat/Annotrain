namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 当前工作库的位置：启动时定死（<c>--db</c> / 环境变量 <c>ANNOTRAIN_DB</c> / 数据目录），运行期不换库。
/// <para>
/// 官方实现就是 <see cref="StartupOptions"/>（也在 Core）：标注端、训练端、命令行导入各自
/// <c>StartupOptions.Parse</c> 一份自己的参数注册进去；测试想单指一个库时同样造一份，不用另写实现。
/// </para>
/// <para>
/// 只需要「库在哪」的地方注入它；要读写数据就注入 <c>IFreeSql</c>——别为了一个字符串去依赖
/// <c>TinyLabelDatabase</c>，那会把开库、建表、释放一起带进来。
/// </para>
/// </summary>
public interface IDatabaseLocation
{
    /// <summary>库文件绝对路径。</summary>
    string DatabasePath { get; }

    /// <summary>库所在目录（默认就是数据目录；用 <c>--db</c> 指到别处时以库为准）。</summary>
    string DatabaseDirectory => System.IO.Path.GetDirectoryName(DatabasePath) ?? AppDataPaths.DataDirectory;
}
