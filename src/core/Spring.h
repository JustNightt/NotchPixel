#pragma once
#include <cmath>
#include <algorithm>

namespace np {

// ============================================================================
// Exact 240 Hz Second-Order Spring Physics Integrator + Smooth Cubic Easing
// & Whole-Pixel Snapping Utilities
// ============================================================================
struct SpringSpec {
    float stiffness;
    float damping;
    float mass;
};

namespace SpringPresets {
    constexpr SpringSpec Open  = { 223.8f, 23.94f, 1.0f };
    constexpr SpringSpec Close = { 195.0f, 27.93f, 1.0f };
    constexpr SpringSpec Fade  = { 438.6f, 41.89f, 1.0f };
    constexpr SpringSpec Panel = { 815.7f, 57.12f, 1.0f };
}

// Smooth time-based cubic easing helpers
inline float CubicEaseOut(float t) {
    t = std::clamp(t, 0.0f, 1.0f);
    float inv = 1.0f - t;
    return 1.0f - (inv * inv * inv);
}

inline float CubicEaseInOut(float t) {
    t = std::clamp(t, 0.0f, 1.0f);
    return (t < 0.5f)
        ? (4.0f * t * t * t)
        : (1.0f - std::pow(-2.0f * t + 2.0f, 3.0f) * 0.5f);
}

// Snaps a DIP coordinate to the nearest whole physical device pixel
inline float SnapDipToWholePixel(float dip, float dpiScale) {
    if (dpiScale <= 0.01f) dpiScale = 1.0f;
    return std::round(dip * dpiScale) / dpiScale;
}

class Spring {
public:
    float value = 0.0f;
    float target = 0.0f;
    float velocity = 0.0f;

    explicit Spring(float initial = 0.0f)
        : value(initial), target(initial), velocity(0.0f) {}

    void Snap(float v) {
        value = v;
        target = v;
        velocity = 0.0f;
    }

    void SetTarget(float t, bool reduceMotion = false) {
        target = t;
        if (reduceMotion) {
            Snap(t);
        }
    }

    bool IsSettled(float eps = 0.03f) const {
        return std::fabs(target - value) <= eps && std::fabs(velocity) <= eps;
    }

    // Steps at 240 Hz fixed substeps (dt = 1/240), returns true if still moving
    bool Step(float elapsedSec, const SpringSpec& spec, float speedMultiplier = 1.0f, bool reduceMotion = false) {
        if (reduceMotion) {
            Snap(target);
            return false;
        }
        if (IsSettled()) {
            value = target;
            velocity = 0.0f;
            return false;
        }

        float dtTotal = std::clamp(elapsedSec * speedMultiplier, 0.001f, 0.064f);
        constexpr float h = 1.0f / 240.0f;
        int steps = std::max(1, static_cast<int>(std::ceil(dtTotal / h)));
        float dt = dtTotal / static_cast<float>(steps);

        for (int i = 0; i < steps; ++i) {
            float displacement = value - target;
            float accel = (-spec.stiffness * displacement - spec.damping * velocity) / spec.mass;
            velocity += accel * dt;
            value += velocity * dt;
        }

        if (IsSettled()) {
            value = target;
            velocity = 0.0f;
            return false;
        }
        return true;
    }
};

} // namespace np
