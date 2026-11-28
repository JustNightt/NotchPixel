package com.openpixel.deck.ui;

import android.animation.ValueAnimator;

/**
 * Motion primitives translated from Apple's "Designing Fluid Interfaces".
 *
 * <p>Springs are described the way Apple describes them — a damping ratio and a
 * response time — instead of raw stiffness/damping masses. That keeps every
 * transition in the app interruptible and velocity aware: retargeting a spring
 * mid-flight continues from the current on-screen value and current velocity.</p>
 */
public final class Motion {

    /** No overshoot. The default for interface transitions. */
    public static final float DAMPING_CRITICAL = 1.0f;
    /** Slight overshoot, only for motion that carried the user's own momentum. */
    public static final float DAMPING_MOMENTUM = 0.82f;

    /** Settle time for small UI transitions (seconds). */
    public static final float RESPONSE_FAST = 0.22f;
    /** Settle time for repositioning surfaces (seconds). */
    public static final float RESPONSE_UI = 0.34f;

    private static volatile int reducedCache = -1;

    private Motion() {}

    /**
     * True when the platform asks for reduced motion (animator scale 0 / battery saver).
     * Callers should cross-fade or snap instead of sliding.
     */
    public static boolean reduced() {
        int cached = reducedCache;
        if (cached >= 0) return cached == 1;
        boolean reduced;
        try {
            reduced = !ValueAnimator.areAnimatorsEnabled();
        } catch (Throwable ignored) {
            reduced = false;
        }
        reducedCache = reduced ? 1 : 0;
        return reduced;
    }

    /** Re-read the platform motion setting (e.g. after returning to the foreground). */
    public static void refreshReducedMotion() {
        reducedCache = -1;
    }

    /**
     * Apple's exponential-decay momentum projection: where a flick would come to rest.
     *
     * @param velocity     release velocity in px/s
     * @param deceleration 0.998 for a normal scroll feel, 0.99 for something snappier
     */
    public static float project(float velocity, float deceleration) {
        return (velocity / 1000f) * deceleration / (1f - deceleration);
    }

    /** Progressive resistance past a boundary instead of a hard stop. */
    public static float rubberband(float overshoot, float dimension) {
        return rubberband(overshoot, dimension, 0.55f);
    }

    public static float rubberband(float overshoot, float dimension, float constant) {
        float d = Math.max(1f, dimension);
        return (overshoot * d * constant) / (d + constant * Math.abs(overshoot));
    }

    public static int blend(int from, int to, float t) {
        float clamped = Math.max(0f, Math.min(1f, t));
        return android.graphics.Color.argb(
                Math.round(android.graphics.Color.alpha(from)
                        + (android.graphics.Color.alpha(to) - android.graphics.Color.alpha(from)) * clamped),
                Math.round(android.graphics.Color.red(from)
                        + (android.graphics.Color.red(to) - android.graphics.Color.red(from)) * clamped),
                Math.round(android.graphics.Color.green(from)
                        + (android.graphics.Color.green(to) - android.graphics.Color.green(from)) * clamped),
                Math.round(android.graphics.Color.blue(from)
                        + (android.graphics.Color.blue(to) - android.graphics.Color.blue(from)) * clamped));
    }

    /** A single scalar spring, stepped by the view that owns it. */
    public static final class Spring {
        public float value;
        public float velocity;
        public float target;

        private float stiffness;
        private float damping;

        public Spring(float dampingRatio, float response) {
            configure(dampingRatio, response);
        }

        public void configure(float dampingRatio, float response) {
            float r = Math.max(0.05f, response);
            float omega = (float) (Math.PI * 2.0) / r;
            stiffness = omega * omega;
            damping = 2f * dampingRatio * omega;
        }

        public void snapTo(float v) {
            value = v;
            velocity = 0f;
            target = v;
        }

        public void to(float t) {
            target = t;
        }

        public boolean settled() {
            return Math.abs(target - value) < 0.0008f && Math.abs(velocity) < 0.02f;
        }

        /** @return true while the spring still needs another frame. */
        public boolean step(float dt) {
            if (dt <= 0f) return !settled();
            float clamped = Math.min(dt, 0.05f);
            float force = (target - value) * stiffness - velocity * damping;
            velocity += force * clamped;
            value += velocity * clamped;
            if (settled()) {
                value = target;
                velocity = 0f;
                return false;
            }
            return true;
        }
    }
}
