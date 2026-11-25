using System;

namespace OpenPixel.Core
{
    public readonly struct SpringSpec
    {
        public readonly float Stiffness;
        public readonly float Damping;
        public readonly float Mass;

        public SpringSpec(float stiffness, float damping, float mass = 1.0f)
        {
            Stiffness = stiffness;
            Damping = damping;
            Mass = mass;
        }
    }

    public static class SpringPresets
    {
        public static readonly SpringSpec Open  = new(223.8f, 23.94f, 1.0f);
        public static readonly SpringSpec Close = new(195.0f, 27.93f, 1.0f);
        public static readonly SpringSpec Fade  = new(438.6f, 41.89f, 1.0f);
        public static readonly SpringSpec Panel = new(815.7f, 57.12f, 1.0f);

        public static float CubicEaseOut(float t)
        {
            t = Math.Clamp(t, 0.0f, 1.0f);
            float inv = 1.0f - t;
            return 1.0f - (inv * inv * inv);
        }

        public static float CubicEaseInOut(float t)
        {
            t = Math.Clamp(t, 0.0f, 1.0f);
            return (t < 0.5f)
                ? (4.0f * t * t * t)
                : (1.0f - MathF.Pow(-2.0f * t + 2.0f, 3.0f) * 0.5f);
        }

        public static float SnapDipToWholePixel(float dip, float dpiScale)
        {
            if (dpiScale <= 0.01f) dpiScale = 1.0f;
            return MathF.Round(dip * dpiScale) / dpiScale;
        }
    }

    public sealed class Spring
    {
        public float Value;
        public float Target;
        public float Velocity;

        public Spring(float initial = 0.0f)
        {
            Value = initial;
            Target = initial;
            Velocity = 0.0f;
        }

        public void Snap(float v)
        {
            Value = v;
            Target = v;
            Velocity = 0.0f;
        }

        public void SetTarget(float t, bool reduceMotion = false)
        {
            Target = t;
            if (reduceMotion)
            {
                Snap(t);
            }
        }

        public bool IsSettled(float eps = 0.03f)
        {
            return MathF.Abs(Target - Value) <= eps && MathF.Abs(Velocity) <= eps;
        }

        public bool Step(float elapsedSec, in SpringSpec spec, float speedMultiplier = 1.0f, bool reduceMotion = false)
        {
            if (reduceMotion)
            {
                Snap(Target);
                return false;
            }
            if (IsSettled())
            {
                Value = Target;
                Velocity = 0.0f;
                return false;
            }

            float dtTotal = Math.Clamp(elapsedSec * speedMultiplier, 0.001f, 0.064f);
            const float h = 1.0f / 240.0f;
            int steps = Math.Max(1, (int)MathF.Ceiling(dtTotal / h));
            float dt = dtTotal / steps;

            for (int i = 0; i < steps; ++i)
            {
                float displacement = Value - Target;
                float accel = (-spec.Stiffness * displacement - spec.Damping * Velocity) / spec.Mass;
                Velocity += accel * dt;
                Value += Velocity * dt;
            }

            if (IsSettled())
            {
                Value = Target;
                Velocity = 0.0f;
                return false;
            }
            return true;
        }
    }
}
