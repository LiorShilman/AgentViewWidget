using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentLiveWidget;

/// <summary>
/// Animated status centerpiece: a glowing core with a rotating ring and
/// orbiting particles. Replaces a flat status icon with a living visual
/// whose color, pulse rate, spin speed, and particle activity all read as
/// "what is the agent doing right now" at a glance.
/// </summary>
public partial class EnergyCore : UserControl
{
    private Storyboard? _current;

    public EnergyCore()
    {
        InitializeComponent();
    }

    public void Apply(Color accent, string statusKey)
    {
        HaloStop0.Color = Color.FromArgb(0x80, accent.R, accent.G, accent.B);
        HaloStop1.Color = Color.FromArgb(0x00, accent.R, accent.G, accent.B);
        RingBrush.Color = accent;
        CoreStop1.Color = accent;
        CoreStop2.Color = Color.FromArgb(0x00, accent.R, accent.G, accent.B);
        Particle1Brush.Color = accent;
        Particle2Brush.Color = accent;
        Particle3Brush.Color = accent;

        _current?.Stop();
        var sb = new Storyboard();

        switch (statusKey)
        {
            case "idle":
                AddPulse(sb, 0.92, 1.04, 2.4);
                AddRotate(sb, RingRotate, 16, clockwise: true);
                AddRotate(sb, ParticleRotate, 22, clockwise: false);
                Ring.Opacity = 0.45;
                ParticleGroup.Opacity = 0.2;
                break;

            case "thinking":
                AddPulse(sb, 0.85, 1.15, 1.1);
                AddRotate(sb, RingRotate, 5, clockwise: true);
                AddRotate(sb, ParticleRotate, 3.2, clockwise: false);
                Ring.Opacity = 0.75;
                ParticleGroup.Opacity = 0.65;
                break;

            case "running_tool":
                AddPulse(sb, 0.9, 1.22, 0.5);
                AddRotate(sb, RingRotate, 1.1, clockwise: true);
                AddRotate(sb, ParticleRotate, 0.9, clockwise: true);
                Ring.Opacity = 1.0;
                ParticleGroup.Opacity = 1.0;
                break;

            case "waiting_approval":
                AddPulse(sb, 0.8, 1.32, 0.6);
                AddRotate(sb, RingRotate, 3.5, clockwise: true);
                AddRotate(sb, ParticleRotate, 4, clockwise: false);
                Ring.Opacity = 0.9;
                ParticleGroup.Opacity = 0.85;
                break;

            default: // offline
                CoreScale.ScaleX = 0.75;
                CoreScale.ScaleY = 0.75;
                Ring.Opacity = 0.18;
                ParticleGroup.Opacity = 0;
                break;
        }

        if (statusKey != "offline")
        {
            sb.Begin();
        }
        _current = sb;
    }

    private void AddPulse(Storyboard sb, double from, double to, double seconds)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        var animX = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        };
        var animY = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        };

        Storyboard.SetTarget(animX, CoreScale);
        Storyboard.SetTargetProperty(animX, new PropertyPath(ScaleTransform.ScaleXProperty));
        Storyboard.SetTarget(animY, CoreScale);
        Storyboard.SetTargetProperty(animY, new PropertyPath(ScaleTransform.ScaleYProperty));

        sb.Children.Add(animX);
        sb.Children.Add(animY);
    }

    private static void AddRotate(Storyboard sb, RotateTransform target, double seconds, bool clockwise)
    {
        var anim = new DoubleAnimation(0, clockwise ? 360 : -360, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(RotateTransform.AngleProperty));
        sb.Children.Add(anim);
    }
}
