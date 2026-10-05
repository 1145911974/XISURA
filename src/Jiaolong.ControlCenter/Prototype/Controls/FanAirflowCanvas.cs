using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed class FanAirflowCanvas : UserControl
{
    private const int ParticleCapacityPerEmitter = 18;
    private readonly CanvasControl surface = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false };
    private readonly Particle[] cpuParticles = new Particle[ParticleCapacityPerEmitter];
    private readonly Particle[] gpuParticles = new Particle[ParticleCapacityPerEmitter];
    private readonly Random random = new(0x4A4C);
    private double cpuStrength;
    private double gpuStrength;
    private float ribbonPhase;

    private enum ParticleKind { Dot, ShortLine, LongSoft, Haze }

    private struct Particle
    {
        public float Progress;
        public float BaseY;
        public float Speed;
        public float Length;
        public float Life;
        public float Age;
        public float Opacity;
        public float Phase;
        public float Frequency;
        public float Amplitude;
        public bool TowardCenter;
        public ParticleKind Kind;
    }

    public FanAirflowCanvas()
    {
        Content = surface;
        surface.Draw += Draw;
        SizeChanged += (_, _) => surface.Invalidate();
        for (int index = 0; index < ParticleCapacityPerEmitter; index++)
        {
            Reset(ref cpuParticles[index], index / (float)ParticleCapacityPerEmitter, index % 2 == 0);
            Reset(ref gpuParticles[index], index / (float)ParticleCapacityPerEmitter, index % 2 == 0);
        }
        Unloaded += (_, _) => surface.RemoveFromVisualTree();
    }

    public void SetStrength(double cpu, double gpu)
    {
        cpuStrength = Math.Clamp(cpu, 0d, 1d);
        gpuStrength = Math.Clamp(gpu, 0d, 1d);
    }

    public void Advance(TimeSpan delta)
    {
        float seconds = (float)Math.Clamp(delta.TotalSeconds, 0d, 1d / 15d);
        ribbonPhase = (ribbonPhase + seconds * (0.32f + 0.7f * (float)Math.Max(cpuStrength, gpuStrength))) % MathF.Tau;
        AdvanceEmitter(cpuParticles, cpuStrength, seconds);
        AdvanceEmitter(gpuParticles, gpuStrength, seconds);
        if (surface.ReadyToDraw) surface.Invalidate();
    }

    public void ResetClock() => surface.Invalidate();

    public void Suspend()
    {
        cpuStrength = 0;
        gpuStrength = 0;
        surface.Invalidate();
    }

    private static void AdvanceEmitter(Particle[] particles, double strength, float seconds)
    {
        int active = FanMotionMath.MapParticleCount(strength);
        for (int index = 0; index < active; index++)
        {
            ref Particle particle = ref particles[index];
            particle.Age += seconds;
            particle.Progress += particle.Speed * seconds * (0.38f + (float)strength * 0.92f);
            if (particle.Age >= particle.Life || particle.Progress >= 1)
                particle.Progress = 1;
        }
    }

    private void Reset(ref Particle particle, float progress, bool towardCenter)
    {
        particle.Progress = progress;
        particle.BaseY = Next(-1, 1);
        particle.Speed = Next(0.17f, 0.35f);
        particle.Length = Next(2.5f, 8.5f);
        particle.Life = Next(1.7f, 3.6f);
        particle.Age = progress * particle.Life;
        particle.Opacity = Next(0.16f, 0.46f);
        particle.Phase = Next(0, MathF.Tau);
        particle.Frequency = Next(0.7f, 1.45f);
        particle.Amplitude = Next(2, 6);
        particle.TowardCenter = towardCenter;
        double kind = random.NextDouble();
        particle.Kind = kind < 0.28 ? ParticleKind.Dot : kind < 0.67 ? ParticleKind.ShortLine : kind < 0.94 ? ParticleKind.LongSoft : ParticleKind.Haze;
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        if (width <= 0 || height <= 0) return;

        DrawAmbientMist(args.DrawingSession, width, height, false, cpuStrength);
        DrawAmbientMist(args.DrawingSession, width, height, true, gpuStrength);
        DrawRibbonFlow(args.DrawingSession, width, height, false, cpuStrength, ribbonPhase);
        DrawRibbonFlow(args.DrawingSession, width, height, true, gpuStrength, ribbonPhase + 1.7f);
        DrawCentralConvergence(args.DrawingSession, width, height, cpuStrength, gpuStrength, ribbonPhase);
        DrawEmitter(args.DrawingSession, cpuParticles, width, height, false, cpuStrength);
        DrawEmitter(args.DrawingSession, gpuParticles, width, height, true, gpuStrength);
    }

    private static void DrawAmbientMist(CanvasDrawingSession drawing, float width, float height, bool gpu, double strength)
    {
        if (strength <= 0.01) return;
        float fanX = width * (gpu ? 0.80f : 0.20f);
        float fanY = height * 0.46f;
        byte red = gpu ? (byte)0 : (byte)255;
        byte green = gpu ? (byte)212 : (byte)36;
        byte blue = gpu ? (byte)255 : (byte)79;
        for (int layer = 4; layer >= 1; layer--)
        {
            float radius = width * (0.055f + layer * 0.026f);
            float alpha = (0.006f + 0.011f * (float)strength) * (5 - layer);
            drawing.FillCircle(fanX, fanY, radius, Color.FromArgb(ToByte(alpha), red, green, blue));
        }
    }

    private static void DrawRibbonFlow(CanvasDrawingSession drawing, float width, float height, bool gpu, double strength, float phase)
    {
        if (strength <= 0.015) return;
        float fanX = width * (gpu ? 0.80f : 0.20f);
        float centerEdge = width * (gpu ? 0.61f : 0.39f);
        float outerEdge = gpu ? width * 1.01f : -width * 0.01f;
        float fanY = height * 0.46f;

        DrawRibbon(drawing, fanX, outerEdge, fanY - 24, -12, gpu, strength, phase, 0);
        DrawRibbon(drawing, fanX, outerEdge, fanY + 22, 18, gpu, strength * 0.82, phase, 1);
        DrawRibbon(drawing, fanX, centerEdge, fanY - 13, -4, gpu, strength * 0.72, phase, 2);
        DrawRibbon(drawing, fanX, centerEdge, fanY + 16, 7, gpu, strength * 0.62, phase, 3);
    }

    private static void DrawCentralConvergence(CanvasDrawingSession drawing, float width, float height, double cpu, double gpu, float phase)
    {
        if (cpu <= 0.02 && gpu <= 0.02) return;
        float y = height * 0.46f;
        DrawRibbon(drawing, width * 0.39f, width * 0.505f, y - 4, 5, false, cpu * 0.28, phase, 5);
        DrawRibbon(drawing, width * 0.61f, width * 0.495f, y + 5, -5, true, gpu * 0.28, phase + 0.8f, 6);
    }

    private static void DrawRibbon(CanvasDrawingSession drawing, float startX, float endX, float startY, float bend, bool gpu, double strength, float phase, int lane)
    {
        if (strength <= 0.01) return;
        const int segments = 26;
        float previousX = startX;
        float previousY = startY;
        for (int segment = 1; segment <= segments; segment++)
        {
            float progress = segment / (float)segments;
            float smooth = progress * progress * (3 - 2 * progress);
            float x = startX + (endX - startX) * smooth;
            float turbulence = MathF.Sin(progress * MathF.PI + phase + lane * 0.73f) * (1.5f + 2.8f * progress);
            float y = startY + bend * MathF.Sin(progress * MathF.PI) + turbulence;
            float endFade = MathF.Sin(MathF.PI * Math.Clamp(progress, 0.03f, 0.97f));
            float alpha = (0.05f + 0.08f * (float)strength) * endFade;
            byte red = gpu ? (byte)0 : (byte)255;
            byte green = gpu ? (byte)214 : (byte)47;
            byte blue = gpu ? (byte)255 : (byte)86;
            drawing.DrawLine(previousX, previousY, x, y, Color.FromArgb(ToByte(alpha * 0.16f), red, green, blue), 24f + 9f * (float)strength);
            drawing.DrawLine(previousX, previousY, x, y, Color.FromArgb(ToByte(alpha * 0.34f), red, green, blue), 10f + 5f * (float)strength);
            drawing.DrawLine(previousX, previousY, x, y, Color.FromArgb(ToByte(alpha), red, green, blue), 1.4f + 0.8f * (float)strength);
            previousX = x;
            previousY = y;
        }
    }

    private void DrawEmitter(CanvasDrawingSession drawing, Particle[] particles, float width, float height, bool gpu, double strength)
    {
        int active = FanMotionMath.MapParticleCount(strength);
        float fanX = width * (gpu ? 0.80f : 0.20f);
        float fanY = height * 0.46f;
        for (int index = 0; index < active; index++)
        {
            ref Particle particle = ref particles[index];
            if (particle.Progress >= 1 || particle.Age >= particle.Life)
                Reset(ref particle, 0, particle.TowardCenter);

            float endX = particle.TowardCenter ? width * (gpu ? 0.59f : 0.41f) : (gpu ? width : 0);
            float x = fanX + (endX - fanX) * particle.Progress;
            float spread = 6f + MathF.Abs(x - fanX) * (0.09f + 0.035f * (float)strength);
            float y = fanY + particle.BaseY * spread + MathF.Sin(particle.Phase + particle.Age * particle.Frequency) * particle.Amplitude;
            float edgeFade = MathF.Sin(MathF.PI * Math.Clamp(particle.Progress, 0, 1));
            float zoneFade = particle.TowardCenter ? FadeForInformationZone(x, width) : 1f;
            float alpha = particle.Opacity * edgeFade * zoneFade * (0.34f + 0.48f * (float)strength);
            if (alpha < 0.01f) continue;

            Color color = gpu
                ? Color.FromArgb(ToByte(alpha), 0, 218, 255)
                : Color.FromArgb(ToByte(alpha), 255, 47, 86);
            float tail = particle.Length * (0.72f + 1.05f * (float)strength);
            float direction = MathF.Sign(endX - fanX);
            switch (particle.Kind)
            {
                case ParticleKind.Dot:
                    drawing.FillCircle(x, y, 0.7f + 0.45f * (float)strength, color);
                    break;
                case ParticleKind.Haze:
                    drawing.FillCircle(x, y, 3.2f + tail * 0.16f, Color.FromArgb(ToByte(alpha * 0.14f), color.R, color.G, color.B));
                    break;
                default:
                    float thickness = particle.Kind == ParticleKind.LongSoft ? 1.5f : 0.9f;
                    drawing.DrawLine(x, y, x - direction * tail, y - particle.BaseY, Color.FromArgb(ToByte(alpha * 0.18f), color.R, color.G, color.B), thickness + 2.2f);
                    drawing.DrawLine(x, y, x - direction * tail, y - particle.BaseY, color, thickness);
                    break;
            }
        }
    }

    private static float FadeForInformationZone(float x, float width)
    {
        float distance = MathF.Abs(x - width * 0.5f) - width * 0.09f;
        return Math.Clamp(distance / (width * 0.055f), 0, 1);
    }

    private static byte ToByte(float alpha) => (byte)Math.Clamp((int)Math.Round(alpha * 255), 0, 255);
    private float Next(float min, float max) => min + (float)random.NextDouble() * (max - min);
}
