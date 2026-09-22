using System;

namespace alpha_island.Animation
{
    /// <summary>
    /// 一维阻尼弹簧积分器，用来做 iOS 灵动岛那种"过冲挤压 → 阻尼回弹归位"的凝胶形变。
    ///
    /// 为什么不用 WPF 的 BackEase / ElasticEase：
    ///   * BackEase 只过冲一次且过冲量固定，形变过程中没有速度耦合，看起来是"直线弹一下"；
    ///   * ElasticEase 会在末尾反复来回振荡，是橡皮筋而不是凝胶。
    /// 弹簧模型能让多个维度（宽、高、圆角、位移、光影相位）用同一组物理参数驱动，
    /// 它们天然同相位、能量守恒，这才是"胶体受外力冲击"的观感来源。
    ///
    /// 参数含义：ω = sqrt(Stiffness/Mass) 是无阻尼角频率，ζ = Damping / (2*sqrt(Stiffness*Mass)) 是阻尼比。
    ///   ζ &lt; 1 才会过冲，过冲量 = exp(-πζ / sqrt(1-ζ²))。
    ///   默认 ζ≈0.63 → 过冲 ≈ 7.6%，4/(ζω) ≈ 0.28s 内基本稳定。符合"小幅过冲 + 柔和短促"。
    /// </summary>
    public sealed class GelSpring
    {
        /// <summary>劲度系数 k，越大越快。</summary>
        public double Stiffness { get; set; } = 420.0;

        /// <summary>阻尼系数 c。</summary>
        public double Damping { get; set; } = 26.0;

        public double Mass { get; set; } = 1.0;

        /// <summary>当前值（0 = 收起，1 = 完全展开）。</summary>
        public double Value { get; private set; }

        /// <summary>当前速度，单位 1/s。</summary>
        public double Velocity { get; private set; }

        /// <summary>目标值（通常是 0 或 1）。</summary>
        public double Target { get; set; }

        /// <summary>当前理论过冲比例，仅用于调试输出。</summary>
        public double Zeta
        {
            get
            {
                double denom = 2.0 * Math.Sqrt(Stiffness * Mass);
                return denom <= 0 ? 1.0 : Damping / denom;
            }
        }

        public void Reset(double value, double velocity = 0)
        {
            Value = value;
            Velocity = velocity;
        }

        /// <summary>
        /// 推进一步。dt 单位秒。内部按最大 2ms 分子步积分 ——
        /// 半隐式欧拉在 dt 过大时会发散（值直接爆成几万），必须限步长。
        /// </summary>
        /// <returns>true 表示还在动；false 表示已经静止在目标上。</returns>
        public bool Step(double dt)
        {
            if (dt <= 0) dt = 0.001;
            if (dt > 0.25) dt = 0.25;   // 卡顿保护：掉帧时不要把积分器推爆

            const double maxSubStep = 0.002;
            int steps = (int)Math.Ceiling(dt / maxSubStep);
            double h = dt / steps;

            for (int i = 0; i < steps; i++)
            {
                double displacement = Value - Target;
                double acceleration = (-Stiffness * displacement - Damping * Velocity) / Mass;
                Velocity += acceleration * h;
                Value += Velocity * h;
            }

            // 静止判定：位移和速度同时足够小就吸附到目标，避免永远在最后 1e-6 上抖。
            if (Math.Abs(Value - Target) < 0.0005 && Math.Abs(Velocity) < 0.01)
            {
                Value = Target;
                Velocity = 0;
                return false;
            }

            return true;
        }
    }
}
