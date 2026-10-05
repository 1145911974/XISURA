namespace Jiaolong_ControlCenter.Services;

public sealed record MotionToken(TimeSpan Duration, double Translation, double Scale);

public static class MotionTokenResolver
{
    public static IReadOnlyList<MotionToken> Resolve(bool reducedMotion) => reducedMotion
        ?
        [
            new(TimeSpan.FromMilliseconds(80), 0, 1),
            new(TimeSpan.FromMilliseconds(100), 0, 1),
            new(TimeSpan.FromMilliseconds(80), 0, 1),
            new(TimeSpan.FromMilliseconds(100), 0, 1)
        ]
        :
        [
            new(TimeSpan.FromMilliseconds(625), 0, 1),
            new(TimeSpan.FromMilliseconds(190), 8, 1),
            new(TimeSpan.FromMilliseconds(150), 0, 1),
            new(TimeSpan.FromSeconds(10), 0, 1.01)
        ];
}
