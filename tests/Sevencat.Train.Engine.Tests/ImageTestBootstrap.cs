using System.Runtime.CompilerServices;
using ImageFacade.SkiaSharp;
using Sevencat.Core.Imaging;

namespace Sevencat.Train.Engine.Tests;

/// <summary>
/// 测试进程的图像后端：与 exe 入口保持一致，注册 SkiaSharp（幂等，重复调用只生效第一次）。
/// </summary>
internal static class ImageTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() => ImageRuntime.Use(new SkiaBackend());
}
