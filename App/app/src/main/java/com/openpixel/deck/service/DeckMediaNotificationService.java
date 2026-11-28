package com.openpixel.deck.service;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.media.MediaMetadata;
import android.media.session.MediaSession;
import android.media.session.PlaybackState;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.SystemClock;

import com.openpixel.deck.R;
import com.openpixel.deck.security.PinnedDeckClient;
import com.openpixel.deck.security.SecurePairingStore;
import com.openpixel.deck.ui.MainActivity;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Lock-screen & shade media notification mirroring PC playback; auto-stops after 10 minutes idle.
 */
public final class DeckMediaNotificationService extends Service {
    public static final String ACTION_UPDATE = "com.openpixel.deck.MEDIA_UPDATE";
    public static final String ACTION_PREV = "com.openpixel.deck.MEDIA_PREV";
    public static final String ACTION_PLAY = "com.openpixel.deck.MEDIA_PLAY";
    public static final String ACTION_NEXT = "com.openpixel.deck.MEDIA_NEXT";
    public static final String ACTION_STOP = "com.openpixel.deck.MEDIA_STOP";

    private static final String CHANNEL_ID = "np_deck_media_v1";
    private static final int NOTIF_ID = 4782;
    private static final long TEN_MINUTES_MS = 10L * 60L * 1000L;

    private MediaSession mediaSession;
    private Handler handler;
    private ExecutorService netExecutor;
    private PinnedDeckClient deckClient;
    private Bitmap brandLargeIcon;

    private String title = "Connected to PC";
    private String artist = "OpenPixel Deck";
    private boolean isPlaying = false;
    private long lastActiveElapsedMs = 0L;

    private final Runnable idleWatchdog = new Runnable() {
        @Override
        public void run() {
            if (!isPlaying && (SystemClock.elapsedRealtime() - lastActiveElapsedMs) >= TEN_MINUTES_MS) {
                stopForeground(true);
                stopSelf();
                return;
            }
            handler.postDelayed(this, 30_000L);
        }
    };

    public static void updatePlayback(Context ctx, String title, String artist, boolean playing) {
        Intent it = new Intent(ctx, DeckMediaNotificationService.class);
        it.setAction(ACTION_UPDATE);
        it.putExtra("title", title);
        it.putExtra("artist", artist);
        it.putExtra("playing", playing);
        if (Build.VERSION.SDK_INT >= 26) {
            ctx.startForegroundService(it);
        } else {
            ctx.startService(it);
        }
    }

    public static void stopService(Context ctx) {
        Intent it = new Intent(ctx, DeckMediaNotificationService.class);
        it.setAction(ACTION_STOP);
        ctx.stopService(it);
    }

    @Override
    public void onCreate() {
        super.onCreate();
        handler = new Handler(Looper.getMainLooper());
        netExecutor = Executors.newSingleThreadExecutor();
        SecurePairingStore store = new SecurePairingStore(this);
        deckClient = new PinnedDeckClient(this, store);
        brandLargeIcon = BitmapFactory.decodeResource(getResources(), R.drawable.ic_openpixel_large);
        lastActiveElapsedMs = SystemClock.elapsedRealtime();

        if (Build.VERSION.SDK_INT >= 26) {
            NotificationChannel ch = new NotificationChannel(
                    CHANNEL_ID, "OpenPixel Deck Media", NotificationManager.IMPORTANCE_LOW);
            ch.setDescription("Lock-screen controls for PC playback");
            NotificationManager nm = getSystemService(NotificationManager.class);
            if (nm != null) nm.createNotificationChannel(ch);
        }

        mediaSession = new MediaSession(this, "OpenPixelDeckMedia");
        mediaSession.setCallback(new MediaSession.Callback() {
            @Override public void onPlay() { dispatchRemote("a=play"); }
            @Override public void onPause() { dispatchRemote("a=play"); }
            @Override public void onSkipToNext() { dispatchRemote("a=next"); }
            @Override public void onSkipToPrevious() { dispatchRemote("a=prev"); }
        });
        mediaSession.setActive(true);
        handler.postDelayed(idleWatchdog, 30_000L);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null) {
            String act = intent.getAction();
            if (ACTION_STOP.equals(act)) {
                stopForeground(true);
                stopSelf();
                return START_NOT_STICKY;
            } else if (ACTION_PREV.equals(act)) {
                dispatchRemote("a=prev");
            } else if (ACTION_PLAY.equals(act)) {
                dispatchRemote("a=play");
            } else if (ACTION_NEXT.equals(act)) {
                dispatchRemote("a=next");
            } else if (ACTION_UPDATE.equals(act)) {
                String t = intent.getStringExtra("title");
                String a = intent.getStringExtra("artist");
                boolean p = intent.getBooleanExtra("playing", false);
                title = (t == null || t.isEmpty()) ? "No active PC media" : t;
                artist = (a == null || a.isEmpty()) ? "OpenPixel Deck" : a;
                isPlaying = p;
                if (isPlaying) lastActiveElapsedMs = SystemClock.elapsedRealtime();
            }
        }
        startForeground(NOTIF_ID, buildNotification());
        return START_NOT_STICKY;
    }

    private void dispatchRemote(final String q) {
        lastActiveElapsedMs = SystemClock.elapsedRealtime();
        netExecutor.execute(() -> {
            try { deckClient.sendMediaAction(q); } catch (Exception ignored) { }
        });
    }

    private PendingIntent actionPendingIntent(String action, int reqCode) {
        Intent it = new Intent(this, DeckMediaNotificationService.class);
        it.setAction(action);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE;
        return PendingIntent.getService(this, reqCode, it, flags);
    }

    private Notification buildNotification() {
        mediaSession.setMetadata(new MediaMetadata.Builder()
                .putString(MediaMetadata.METADATA_KEY_TITLE, title)
                .putString(MediaMetadata.METADATA_KEY_ARTIST, artist)
                .build());
        mediaSession.setPlaybackState(new PlaybackState.Builder()
                .setActions(PlaybackState.ACTION_PLAY_PAUSE | PlaybackState.ACTION_SKIP_TO_NEXT | PlaybackState.ACTION_SKIP_TO_PREVIOUS)
                .setState(isPlaying ? PlaybackState.STATE_PLAYING : PlaybackState.STATE_PAUSED, 0L, 1.0f)
                .build());

        Intent openIntent = new Intent(this, MainActivity.class);
        PendingIntent contentPi = PendingIntent.getActivity(
                this, 0, openIntent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Notification.Builder b = (Build.VERSION.SDK_INT >= 26)
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        return b.setSmallIcon(R.drawable.ic_openpixel_notification)
                .setLargeIcon(brandLargeIcon)
                .setContentTitle(title)
                .setContentText(artist)
                .setContentIntent(contentPi)
                .setOngoing(isPlaying)
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_media_previous, "Prev", actionPendingIntent(ACTION_PREV, 1)).build())
                .addAction(new Notification.Action.Builder(
                        isPlaying ? android.R.drawable.ic_media_pause : android.R.drawable.ic_media_play,
                        isPlaying ? "Pause" : "Play", actionPendingIntent(ACTION_PLAY, 2)).build())
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_media_next, "Next", actionPendingIntent(ACTION_NEXT, 3)).build())
                .setStyle(new Notification.MediaStyle()
                        .setMediaSession(mediaSession.getSessionToken())
                        .setShowActionsInCompactView(0, 1, 2))
                .build();
    }

    @Override
    public void onDestroy() {
        handler.removeCallbacks(idleWatchdog);
        if (mediaSession != null) {
            mediaSession.setActive(false);
            mediaSession.release();
        }
        netExecutor.shutdownNow();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
