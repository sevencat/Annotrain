using Microsoft.Extensions.DependencyInjection;

namespace Sevencat.Core.Infrastructure;

/// <summary>
/// 容器（<c>ServiceCollection.BuildServiceProvider()</c> 的结果）的静态出口：宿主启动时放进来一份，
/// 给少数拿不到构造函数注入的地方（XAML 预览、转换器 / 样式里的回调、兜底提示框）偶尔取一次服务。
/// <para>
/// 常规代码一律构造函数注入——服务注册由 Injectio 的 <c>[RegisterSingleton]</c> 在编译期生成，
/// 用到哪个就在构造函数里写哪个。从这里 resolve 是逃生舱口：绕过容器的生命周期与作用域，
/// 也让单测没法替换实现，所以能不用就不用。
/// </para>
/// <para>
/// 进程里可能压根没人调用 <see cref="Initialize"/>（命令行导入、Web 宿主、单元测试），
/// 那时 <see cref="IsReady"/> 为 <c>false</c>、<see cref="Resolve{T}"/> 返回 <c>null</c>。
/// </para>
/// </summary>
public static class BeanFactory
{
    /// <summary>宿主启动时放进来；退出释放容器后置空。</summary>
    private static IServiceProvider? _provider;

    /// <summary>容器是否已经建好（<c>false</c> 时 <see cref="Resolve{T}"/> 只会返回 <c>null</c>）。</summary>
    public static bool IsReady => _provider is not null;

    /// <summary>容器建好后调用一次；重复调用只换引用（重启、测试用）。</summary>
    public static void Initialize(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _provider = provider;
    }

    /// <summary>按类型取服务：容器还没建好或类型没注册都返回 <c>null</c>，不抛。</summary>
    public static T? Resolve<T>() where T : class => _provider?.GetService<T>();

    /// <summary>必须有：容器没建好或类型没注册直接抛。</summary>
    public static T Require<T>() where T : class => GetProvider().GetRequiredService<T>();

    /// <summary>容器释放后清掉引用，免得之后调到已经释放的 provider。</summary>
    public static void Reset() => _provider = null;

    private static IServiceProvider GetProvider() =>
        _provider ?? throw new InvalidOperationException(
            "BeanFactory 还没初始化：启动时先调用 BeanFactory.Initialize(provider)。");
}
