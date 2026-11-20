namespace LiquidGlass.Desktop.Dock;

/// <summary>
/// Dock 放大与重排。
///
/// 算法参考 WinDock 的 LensMath（MIT, Copyright (c) 2026 Breno Alvim），改写为 float 版本。
///
/// 这里**故意不做"指针锚定"整体平移**。曾经的写法是反解一个映射、再让整条横移，
/// 使"指针下那个点"始终停在指针底下。听起来更像 macOS，实测是抖动之源：
/// 指针每移动 1px，同一个图标的位移会在 [-0.45, +0.45] 之间来回（出现反向位移），
/// 而且每跨过一个图标边界，二阶差分就冒出一次 0.45 的尖峰（平滑方案只有 0.009）。
/// 视觉上就是"整条在抖"。
///
/// 现在的做法：位置只由"第 i 个图标之前所有图标的放大宽度之和"决定，
/// 左端固定、向右单向累积。图标只会被邻居推开，绝不反向，导数是连续的。
/// 面板的宽度与位置在槽位变化时就算死，运行期不动 —— 玻璃框完全静止，
/// 动的只有图标本身，这才是稳的感觉。
/// </summary>
internal static class DockLayout
{
    /// <summary>余弦影响域：距离 0 → 1，距离 ≥ radius → 0，两端导数连续。</summary>
    public static float Influence(float distance, float radius)
    {
        if (radius <= 0f || distance >= radius) return 0f;
        return (1f + MathF.Cos(MathF.PI * distance / radius)) * 0.5f;
    }

    public readonly struct Result
    {
        public readonly float[] Left;
        public readonly float[] Scale;
        public readonly float Total;

        public Result(float[] left, float[] scale, float total)
        {
            Left = left;
            Scale = scale;
            Total = total;
        }
    }

    /// <summary>
    /// 每帧都要算一次的临时缓冲。原来这里是 new float[n]，60fps 下就是每秒 60 次小分配，
    /// 攒到触发 gen0 GC 就是一次掉帧。只有渲染线程调用它，用静态缓存即可。
    /// </summary>
    [ThreadStatic] private static float[]? _baseLeftBuffer;

    /// <param name="gaps">每两个槽位之间的间距，长度 = widths.Length - 1。
    /// 分隔线两侧只留半个间距，所以它不会额外撑开图标。</param>
    public static Result Compute(ReadOnlySpan<float> widths, ReadOnlySpan<float> gaps, float pointer, float amplitude,
        float magnification, float radius, float[] left, float[] scale)
    {
        int n = widths.Length;

        float[] baseLeft = _baseLeftBuffer ??= new float[64];
        if (baseLeft.Length < n)
        {
            baseLeft = new float[n];
            _baseLeftBuffer = baseLeft;
        }

        float x = 0f;
        for (int i = 0; i < n; i++)
        {
            baseLeft[i] = x;
            x += widths[i] + (i < n - 1 ? gaps[i] : 0f);
            scale[i] = 1f;
        }

        if (n == 0) return new Result(left, scale, 0f);

        bool magnify = amplitude > 0.001f && !float.IsNaN(pointer);
        if (magnify)
        {
            float grow = (magnification - 1f) * amplitude;
            for (int i = 0; i < n; i++)
            {
                float center = baseLeft[i] + widths[i] * 0.5f;
                scale[i] = 1f + grow * Influence(MathF.Abs(center - pointer), radius);
            }
        }

        x = 0f;
        for (int i = 0; i < n; i++)
        {
            left[i] = x;
            x += widths[i] * scale[i] + (i < n - 1 ? gaps[i] : 0f);
        }

        float total = x;

        return new Result(left, scale, total);
    }

    /// <summary>
    /// 整条 Dock 在"最大放大态"下需要的宽度。
    /// 面板宽度必须按这个值**一次算死**，不能每帧拿实际 total 去改面板 —— 否则
    /// 指针一路扫过去，面板会在 788~811px 之间反复伸缩，加上面板居中，
    /// 左右两端同时抖，玻璃边框整个跟着晃。
    ///
    /// total 是 pointer 的连续函数，最大值落在某个指针位置上，直接采样扫一遍即可。
    /// 只在槽位变化时调用（几十次三角函数，微秒级）。
    /// </summary>
    public static float MaxTotal(ReadOnlySpan<float> widths, ReadOnlySpan<float> gaps, float magnification, float radius)
    {
        int n = widths.Length;
        if (n == 0) return 0f;

        float[] baseLeft = _baseLeftBuffer ??= new float[64];
        if (baseLeft.Length < n)
        {
            baseLeft = new float[n];
            _baseLeftBuffer = baseLeft;
        }

        float x = 0f;
        for (int i = 0; i < n; i++)
        {
            baseLeft[i] = x;
            x += widths[i] + (i < n - 1 ? gaps[i] : 0f);
        }

        float baseTotal = x;
        float grow = magnification - 1f;
        if (grow <= 0f || radius <= 0f) return baseTotal;

        float best = baseTotal;
        const int Samples = 256;
        for (int s = 0; s <= Samples; s++)
        {
            float pointer = baseTotal * s / Samples;
            float total = 0f;
            for (int i = 0; i < n; i++)
            {
                float center = baseLeft[i] + widths[i] * 0.5f;
                float scale = 1f + grow * Influence(MathF.Abs(center - pointer), radius);
                total += widths[i] * scale;
                if (i < n - 1) total += gaps[i];
            }

            if (total > best) best = total;
        }

        return best;
    }
}

/// <summary>半隐式欧拉弹簧，带子步长，保证 120Hz/144Hz 下手感一致。</summary>
internal struct Spring
{
    public float Value;
    public float Velocity;

    private const float SubStep = 1f / 240f;

    public Spring(float value)
    {
        Value = value;
        Velocity = 0f;
    }

    public void StepTo(float target, float dt, float stiffness, float damping)
    {
        if (dt <= 0f) return;
        if (dt > 0.1f) dt = 0.1f;

        int steps = Math.Max(1, (int)MathF.Ceiling(dt / SubStep));
        float h = dt / steps;

        for (int s = 0; s < steps; s++)
        {
            float accel = (target - Value) * stiffness - Velocity * damping;
            Velocity += accel * h;
            Value += Velocity * h;
        }
    }

    public void Snap(float value)
    {
        Value = value;
        Velocity = 0f;
    }

    public bool IsSettled(float target, float epsilon = 0.002f)
    {
        return MathF.Abs(Value - target) < epsilon && MathF.Abs(Velocity) < epsilon;
    }
}
