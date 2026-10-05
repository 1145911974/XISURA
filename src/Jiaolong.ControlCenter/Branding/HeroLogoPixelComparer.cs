namespace Jiaolong_ControlCenter.Branding;

public sealed record HeroLogoPixelDiff(int MaxChannelDelta, double MeanChannelDelta, byte[] HeatmapBgra);

public static class HeroLogoPixelComparer
{
    public static HeroLogoPixelDiff Compare(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (expected.Length != actual.Length || expected.Length % 4 != 0)
            throw new ArgumentException("BGRA buffers must have the same whole-pixel length");

        var heatmap = new byte[expected.Length];
        long sum = 0;
        var max = 0;
        for (var offset = 0; offset < expected.Length; offset += 4)
        {
            var delta = 0;
            for (var channel = 0; channel < 3; channel++)
            {
                var value = Math.Abs(expected[offset + channel] - actual[offset + channel]);
                delta += value;
                max = Math.Max(max, value);
                sum += value;
            }
            heatmap[offset + 2] = (byte)Math.Min(255, delta);
            heatmap[offset + 3] = delta == 0 ? (byte)0 : (byte)255;
        }

        var channelCount = expected.Length / 4 * 3;
        return new(max, channelCount == 0 ? 0 : (double)sum / channelCount, heatmap);
    }
}
