using System;

namespace GravitySimulator.Classic
{
    /// <summary>
    /// Real-time pace of the classic simulator. The GDI renderer held the 17 ms timer back to about
    /// 5 ticks a second, each advancing the physics a fixed 0.4 s, so the simulation ran at about
    /// 2.2 simulated seconds per real second. With OpenGL the timer really ticks about 32 times a second;
    /// rather than stepping 0.4 s each time (about 13 s/s), each tick steps the real time since the
    /// last one times <see cref="SimSecondsPerRealSecond"/>: the old pace, in smaller, more accurate steps.
    /// </summary>
    public static class ClassicPace
    {
        /// <summary>Simulated seconds per real second: the pace measured on the GDI renderer (two 16 s runs: means 2.27 and 2.16, range 2.04 to 2.42).</summary>
        public const float SimSecondsPerRealSecond = 2.2f;

        /// <summary>
        /// The physics step for a tick that came <paramref name="realSeconds"/> after the previous one:
        /// realSeconds * <see cref="SimSecondsPerRealSecond"/>, never more than <paramref name="maxStep"/>
        /// (so a stalled frame cannot produce one huge step; the simulation just falls behind) and never negative.
        /// </summary>
        public static float Step(double realSeconds, float maxStep)
        {
            if (!(realSeconds > 0)) return 0f;
            return (float)Math.Min(realSeconds * SimSecondsPerRealSecond, maxStep);
        }
    }
}
