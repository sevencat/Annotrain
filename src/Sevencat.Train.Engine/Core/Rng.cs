namespace Sevencat.Train.Engine.Core;

/// <summary>
/// 可复现的伪随机数发生器（xorshift64*）。
/// <para>
/// 不用 <see cref="System.Random"/>：它跨 .NET 版本不保证序列稳定，
/// 而训练的可复现性要求「同一个 seed 得到同一批权重、同一份增强」。
/// </para>
/// </summary>
public sealed class Rng
{
    private const ulong DefaultSeed = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    public Rng(ulong seed)
    {
        _state = seed == 0 ? DefaultSeed : seed;
    }

    /// <summary>用当前时间派生一个种子（仅用于「不关心复现」的场景）。</summary>
    public static Rng FromClock() => new((ulong)DateTime.UtcNow.Ticks ^ 0xA5A5_5A5A_1234_5678UL);

    /// <summary>32 位无符号随机数。</summary>
    public uint NextUInt32()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return (uint)((x * 0x2545F4914F6CDD1DUL) >> 32);
    }

    /// <summary>[0, 1) 均匀分布。</summary>
    public float NextFloat() => (NextUInt32() >> 8) * (1.0f / 16777216.0f);

    /// <summary>[min, max) 均匀分布。</summary>
    public float NextFloat(float min, float max) => min + (max - min) * NextFloat();

    /// <summary>[0, maxExclusive) 整数。</summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "上界必须大于 0");
        return (int)(NextUInt32() % (uint)maxExclusive);
    }

    /// <summary>标准正态分布（Box–Muller，缓存另一半样本）。</summary>
    public float NextNormal()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }

        float u1;
        do
        {
            u1 = NextFloat();
        }
        while (u1 <= float.Epsilon);

        float u2 = NextFloat();
        float radius = MathF.Sqrt(-2.0f * MathF.Log(u1));
        float angle = 2.0f * MathF.PI * u2;

        _spare = radius * MathF.Sin(angle);
        _hasSpare = true;
        return radius * MathF.Cos(angle);
    }

    /// <summary>按给定概率返回 true。</summary>
    public bool NextBool(float probability)
    {
        if (probability is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(probability), probability, "概率必须在 [0,1]");
        return NextFloat() < probability;
    }

    private bool _hasSpare;
    private float _spare;
}
