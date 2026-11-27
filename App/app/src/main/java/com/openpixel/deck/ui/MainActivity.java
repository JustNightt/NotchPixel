package com.openpixel.deck.ui;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Outline;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.Base64;
import android.view.Gravity;
import android.view.View;
import android.view.ViewOutlineProvider;
import android.view.inputmethod.EditorInfo;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import com.openpixel.deck.network.WakeOnLanSender;
import com.openpixel.deck.security.PinnedDeckClient;
import com.openpixel.deck.security.SecurePairingStore;
import com.openpixel.deck.service.DeckMediaNotificationService;

import java.net.URLEncoder;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * NotchPixel Deck floating pill control surface for Android.
 * Tabs: Now, Pad, Apps, Modes (Game, Present, Movie, Work), and PC.
 */
public final class MainActivity extends Activity {
    private static final int REQ_PAIR = 2010;
    private static final int REQ_NOTIF = 2011;

    private SecurePairingStore store;
    private PinnedDeckClient client;
    private ExecutorService pollExecutor;
    private ExecutorService cmdExecutor;
    private ExecutorService padUdpExecutor;
    private Handler uiHandler;

    // Coalesced zero-lag UDP trackpad state (impossible to backlog or drift)
    private final AtomicInteger pendingPadDx = new AtomicInteger(0);
    private final AtomicInteger pendingPadDy = new AtomicInteger(0);
    private final AtomicInteger pendingPadScroll = new AtomicInteger(0);
    private final AtomicBoolean padFlushScheduled = new AtomicBoolean(false);

    private int activeTab = 0;      // 0=Now, 1=Pad, 2=Apps, 3=Modes, 4=PC
    private int activeModeSub = 0;  // 0=Game, 1=Present, 2=Movie, 3=Work
    private long presentStartMs = 0L;
    private boolean presentingActive = false;

    private FrameLayout outerFrame;
    private View ambientBackdrop;
    private AudioWaveBackgroundView audioWaveBg;
    private LinearLayout rootLayout;
    private LinearLayout topPillWrap;
    private LinearLayout topPill;
    private View topStatusDot;
    private TextView tvTopStatus, tvTopBadge, tvBannerNotice;
    private Button btnQuickPair;
    private FrameLayout contentHost;
    private LiquidBottomBarView bottomTabBar;
    private final List<LiquidGlassHelper.GlassContainer> glassCards = new ArrayList<>();
    private LiquidStatCardView nowCardCpu, nowCardGpu, nowCardRam;
    private LinearLayout nowWidgetContent;
    private final Button[] nowWidgetBtns = new Button[3];
    private int activeNowWidget = 0; // 0=Clipboard, 1=Files, 2=To-Do
    private FrameLayout nowArtCard;
    private ImageView ivNowArt;
    private TextView tvNowArtGlyph;
    private String lastArtB64 = "";
    private Button btnDeckFullscreen, btnAddSlotRef;
    private LinearLayout appsCardHeader, deckSplitRow, deckSidebar;
    private Button btnSideAudioMute, btnSideMicMute, btnSideTestAudio;
    private boolean deckFullscreen = false;
    private LinearLayout shelfListBox, clipListBox, todoListBox;
    private final Button[] gradientButtons = new Button[5];
    private String lastShelfClipSig = "";
    private LiquidToggleView swAudioWave, swSyncMusicColor;
    private String lastAppsSignature = "";
    private String lastThemeSignature = "";
    private int activeAccentColor = Color.rgb(43, 228, 234);
    private boolean glassSurface = true;

    // Now tab views
    private LinearLayout tabNow;
    private TextView tvMediaTitle, tvMediaArtist, tvMediaApp, tvMediaTime, tvVolLabel;
    private LiquidSliderView sbSeek, sbVolume;
    private Button btnPlayPause, btnMuteVol, btnMuteMic;
    private boolean isSeekingMedia = false;
    private boolean isDraggingVolume = false;
    private long lastSeekSendMs = 0L;
    private long lastVolSendMs = 0L;

    // Pad tab views
    private LinearLayout tabPad;
    private PadCanvasView padCanvas;
    private final Button[] padSpeedBtns = new Button[4];
    private final Button[] scrollSpeedBtns = new Button[4];
    private EditText etPadType;

    // Apps tab views
    private LinearLayout tabApps;
    private GridLayout appsGrid;

    // Modes tab views
    private LinearLayout tabModes;
    private final Button[] modeSubBtns = new Button[4];
    private FrameLayout modeSubHost;
    private TextView tvRecTimer, tvPresentTimer;
    private Button btnGameRecord;

    // PC tab views
    private LinearLayout tabPc;
    private LiquidStatCardView cardCpu, cardGpu, cardRam;
    private HoldPowerButton btnSleep, btnRestart, btnShutdown;
    private TextView tvPairedSummary, tvPcTabNotice;
    private final Button[] lookButtons = new Button[4];
    private LiquidToggleView swTrailToggle, swHapticsToggle, swMediaNotif;

    private PinnedDeckClient.PcState lastState = new PinnedDeckClient.PcState();
    private boolean lightSurface = false;
    private volatile boolean deviceRevoked = false;

    private final Runnable pollRunnable = new Runnable() {
        @Override
        public void run() {
            if (store.isPaired() && !deviceRevoked) {
                pollExecutor.execute(() -> {
                    try {
                        PinnedDeckClient.PcState st = client.fetchState();
                        runOnUiThread(() -> applyStateToUi(st, true));
                    } catch (Exception ex) {
                        boolean is401 = (ex instanceof SecurityException)
                                && ex.getMessage() != null
                                && ex.getMessage().contains("HTTP 401");
                        if (is401) {
                            deviceRevoked = true;
                            runOnUiThread(() -> {
                                DeckMediaNotificationService.stopService(MainActivity.this);
                                applyDisconnectedState("Device removed on PC — tap Pair PC to re-pair.");
                            });
                        } else {
                            runOnUiThread(() -> applyDisconnectedState(ex.getMessage()));
                        }
                    }
                });
                uiHandler.postDelayed(this, 1000L);
            } else if (!store.isPaired()) {
                applyDisconnectedState("Not paired yet — tap Pair PC");
            }
        }
    };

    private void schedulePadFlush() {
        if (!store.isPaired()) return;
        if (padFlushScheduled.compareAndSet(false, true)) {
            padUdpExecutor.execute(() -> {
                try {
                    while (true) {
                        int dx = pendingPadDx.getAndSet(0);
                        int dy = pendingPadDy.getAndSet(0);
                        int sc = pendingPadScroll.getAndSet(0);
                        if (dx != 0 || dy != 0) {
                            client.sendPadUdp("move|" + dx + "|" + dy);
                        }
                        if (sc != 0) {
                            client.sendPadUdp("scroll|" + sc);
                        }
                        if (pendingPadDx.get() == 0 && pendingPadDy.get() == 0 && pendingPadScroll.get() == 0) {
                            break;
                        }
                        Thread.sleep(8);
                    }
                } catch (Exception ignored) {
                } finally {
                    padFlushScheduled.set(false);
                    if (pendingPadDx.get() != 0 || pendingPadDy.get() != 0 || pendingPadScroll.get() != 0) {
                        schedulePadFlush();
                    }
                }
            });
        }
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        store = new SecurePairingStore(this);
        client = new PinnedDeckClient(this, store);
        pollExecutor = Executors.newSingleThreadExecutor();
        cmdExecutor = Executors.newFixedThreadPool(3);
        padUdpExecutor = Executors.newSingleThreadExecutor();
        uiHandler = new Handler(Looper.getMainLooper());

        buildUi();
        applyLookTheme();
        switchTab(0);

        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, REQ_NOTIF);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        // Reload pairing secrets saved by QrPairingActivity so connection starts immediately.
        store.reload();
        refreshPairingSummary();
        uiHandler.removeCallbacks(pollRunnable);
        uiHandler.post(pollRunnable);
    }

    @Override
    protected void onPause() {
        uiHandler.removeCallbacks(pollRunnable);
        super.onPause();
    }

    private float dp(float v) {
        return v * getResources().getDisplayMetrics().density;
    }

    private GradientDrawable pillDrawable(int fill, int stroke, float radDp) {
        GradientDrawable gd = new GradientDrawable();
        gd.setColor(fill);
        gd.setCornerRadius(dp(radDp));
        gd.setStroke(Math.round(dp(1f)), stroke);
        return gd;
    }

    private void buildUi() {
        outerFrame = new FrameLayout(this);

        // Dedicated GPU-accelerated ambient backdrop layer blurred with AGSL Liquid Glass RenderEffect
        ambientBackdrop = new View(this);
        outerFrame.addView(ambientBackdrop, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        // Smooth flowing music-colored audio wave backdrop (Tab 1 Now ONLY)
        audioWaveBg = new AudioWaveBackgroundView(this);
        outerFrame.addView(audioWaveBg, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        rootLayout = new LinearLayout(this);
        rootLayout.setOrientation(LinearLayout.VERTICAL);
        rootLayout.setPadding(Math.round(dp(14)), Math.round(dp(18)), Math.round(dp(14)), Math.round(dp(14)));
        outerFrame.addView(rootLayout, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        // Sleek professional connection status pill (glowing dot + PC name + live badge)
        topPillWrap = new LinearLayout(this);
        topPillWrap.setOrientation(LinearLayout.HORIZONTAL);
        topPillWrap.setGravity(Gravity.CENTER);

        topPill = new LinearLayout(this);
        topPill.setOrientation(LinearLayout.HORIZONTAL);
        topPill.setGravity(Gravity.CENTER_VERTICAL);
        topPill.setPadding(Math.round(dp(14)), Math.round(dp(7)), Math.round(dp(10)), Math.round(dp(7)));
        topPill.setOnClickListener(v -> startActivityForResult(new Intent(this, QrPairingActivity.class), REQ_PAIR));

        topStatusDot = new View(this);
        LinearLayout.LayoutParams dotLp = new LinearLayout.LayoutParams(Math.round(dp(9)), Math.round(dp(9)));
        dotLp.rightMargin = Math.round(dp(9));
        topPill.addView(topStatusDot, dotLp);

        tvTopStatus = new TextView(this);
        tvTopStatus.setText(store.isPaired() ? store.getPcName() : "Pair PC");
        tvTopStatus.setTextSize(13.5f);
        tvTopStatus.setSingleLine(true);
        tvTopStatus.getPaint().setFakeBoldText(true);
        topPill.addView(tvTopStatus);

        tvTopBadge = new TextView(this);
        tvTopBadge.setText(store.isPaired() ? "SYNCING" : "SETUP");
        tvTopBadge.setTextSize(9.5f);
        tvTopBadge.getPaint().setFakeBoldText(true);
        tvTopBadge.setPadding(Math.round(dp(8)), Math.round(dp(3)), Math.round(dp(8)), Math.round(dp(3)));
        LinearLayout.LayoutParams badgeLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        badgeLp.leftMargin = Math.round(dp(10));
        topPill.addView(tvTopBadge, badgeLp);

        btnQuickPair = new Button(this);
        btnQuickPair.setVisibility(View.GONE);
        topPill.addView(btnQuickPair);

        topPillWrap.addView(topPill, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout.LayoutParams topLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        topLp.topMargin = Math.round(dp(6));
        topLp.bottomMargin = Math.round(dp(10));
        rootLayout.addView(topPillWrap, topLp);

        tvBannerNotice = new TextView(this);
        tvBannerNotice.setTextSize(12f);
        tvBannerNotice.setGravity(Gravity.CENTER);
        tvBannerNotice.setPadding(Math.round(dp(8)), 0, Math.round(dp(8)), Math.round(dp(8)));
        tvBannerNotice.setVisibility(View.GONE);
        rootLayout.addView(tvBannerNotice);

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setVerticalScrollBarEnabled(false);
        scroll.setClipToPadding(false);
        scroll.setPadding(0, 0, 0, Math.round(dp(16)));
        contentHost = new FrameLayout(this);
        scroll.addView(contentHost);
        rootLayout.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));

        buildTabNow();
        buildTabPad();
        buildTabApps();
        buildTabModes();
        buildTabPc();

        contentHost.addView(tabNow);
        contentHost.addView(tabPad);
        contentHost.addView(tabApps);
        contentHost.addView(tabModes);
        contentHost.addView(tabPc);

        // Interactive Kyant0-style Liquid Glass sliding bottom taskbar
        bottomTabBar = new LiquidBottomBarView(this);
        bottomTabBar.setOnTabSelectedListener(this::switchTab);
        LinearLayout.LayoutParams botLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        botLp.topMargin = Math.round(dp(10));
        rootLayout.addView(bottomTabBar, botLp);

        setContentView(outerFrame);
    }

    private LinearLayout makeCardContainer() {
        LiquidGlassHelper.GlassContainer ll = new LiquidGlassHelper.GlassContainer(this, 22);
        ll.setOrientation(LinearLayout.VERTICAL);
        ll.setPadding(Math.round(dp(16)), Math.round(dp(16)), Math.round(dp(16)), Math.round(dp(16)));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Math.round(dp(12));
        ll.setLayoutParams(lp);
        ll.setGlassStyle(glassSurface, lightSurface, activeAccentColor);
        glassCards.add(ll);
        return ll;
    }

    private LinearLayout makeLiquidToggleRow(String label, LiquidToggleView toggle) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, Math.round(dp(6)), 0, Math.round(dp(2)));
        TextView tv = new TextView(this);
        tv.setText(label);
        tv.setTextSize(13f);
        tv.setTextColor(Color.argb(225, 238, 240, 246));
        row.addView(tv, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        row.addView(toggle);
        return row;
    }

    private Button makeActionBtn(String text, int bg, int fg) {
        Button b = new Button(this);
        b.setText(text);
        b.setAllCaps(false);
        b.setTextColor(fg);
        b.setTextSize(13f);
        b.setMinHeight(0);
        b.setMinimumHeight(0);
        b.setStateListAnimator(null);
        b.getPaint().setFakeBoldText(true);
        b.setBackground(pillDrawable(bg, Color.argb(24, 255, 255, 255), 14));
        b.setPadding(Math.round(dp(14)), Math.round(dp(11)), Math.round(dp(14)), Math.round(dp(11)));
        return b;
    }

    private void buildTabNow() {
        tabNow = new LinearLayout(this);
        tabNow.setOrientation(LinearLayout.VERTICAL);
        tabNow.setGravity(Gravity.CENTER_HORIZONTAL);

        // Large centered rounded-square album art card (208dp, shows real PC album art + music icon fallback)
        final float artRad = dp(24);
        nowArtCard = new FrameLayout(this);
        nowArtCard.setBackground(pillDrawable(Color.argb(180, 18, 46, 36), Color.argb(46, 255, 255, 255), 24));
        nowArtCard.setClipToOutline(true);
        nowArtCard.setOutlineProvider(new ViewOutlineProvider() {
            @Override public void getOutline(View v, Outline o) { o.setRoundRect(0, 0, v.getWidth(), v.getHeight(), artRad); }
        });
        ivNowArt = new ImageView(this);
        ivNowArt.setScaleType(ImageView.ScaleType.CENTER_CROP);
        ivNowArt.setVisibility(View.GONE);
        nowArtCard.addView(ivNowArt, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        tvNowArtGlyph = new TextView(this);
        tvNowArtGlyph.setText("♪");
        tvNowArtGlyph.setTextSize(54f);
        tvNowArtGlyph.setTextColor(Color.argb(195, 239, 237, 232));
        tvNowArtGlyph.setGravity(Gravity.CENTER);
        nowArtCard.addView(tvNowArtGlyph, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        LinearLayout.LayoutParams artLp = new LinearLayout.LayoutParams(Math.round(dp(208)), Math.round(dp(208)));
        artLp.topMargin = Math.round(dp(6));
        artLp.bottomMargin = Math.round(dp(18));
        tabNow.addView(nowArtCard, artLp);

        tvMediaApp = new TextView(this);
        tvMediaApp.setText("NOW PLAYING ON PC");
        tvMediaApp.setTextSize(10.5f);
        tvMediaApp.setGravity(Gravity.CENTER);
        tvMediaApp.setTextColor(Color.argb(165, 239, 237, 232));
        tabNow.addView(tvMediaApp);

        tvMediaTitle = new TextView(this);
        tvMediaTitle.setText("Nothing playing");
        tvMediaTitle.setTextSize(21f);
        tvMediaTitle.setGravity(Gravity.CENTER);
        tvMediaTitle.getPaint().setFakeBoldText(true);
        tvMediaTitle.setPadding(Math.round(dp(8)), Math.round(dp(4)), Math.round(dp(8)), Math.round(dp(2)));
        tabNow.addView(tvMediaTitle);

        tvMediaArtist = new TextView(this);
        tvMediaArtist.setText("Play music or video on your PC");
        tvMediaArtist.setTextSize(14f);
        tvMediaArtist.setGravity(Gravity.CENTER);
        tabNow.addView(tvMediaArtist);

        sbSeek = new LiquidSliderView(this);
        sbSeek.setMax(100);
        sbSeek.setOnChangeListener(new LiquidSliderView.OnChangeListener() {
            @Override public void onChanged(int p, boolean fromUser) {
                if (fromUser && lastState.durSec > 0) {
                    int sec = (int) (lastState.durSec * (p / 100.0));
                    tvMediaTime.setText(fmtSec(sec) + " / " + fmtSec(lastState.durSec));
                    long now = System.currentTimeMillis();
                    if (now - lastSeekSendMs >= 120L) {
                        lastSeekSendMs = now;
                        sendAsync(() -> client.sendMediaAction("a=seek&v=" + sec));
                    }
                }
            }
            @Override public void onStartTracking() { isSeekingMedia = true; }
            @Override public void onStopTracking(int p) {
                if (lastState.durSec > 0) {
                    int sec = (int) (lastState.durSec * (p / 100.0));
                    sendAsync(() -> client.sendMediaAction("a=seek&v=" + sec));
                }
                isSeekingMedia = false;
            }
        });
        LinearLayout.LayoutParams sLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        sLp.topMargin = Math.round(dp(18));
        tabNow.addView(sbSeek, sLp);

        tvMediaTime = new TextView(this);
        tvMediaTime.setText("0:00 / 0:00");
        tvMediaTime.setTextSize(11.5f);
        tvMediaTime.setGravity(Gravity.CENTER);
        tabNow.addView(tvMediaTime);

        // 3 circular transport buttons (frosted Prev, solid white 64dp Play/Pause, frosted Next)
        LinearLayout transport = new LinearLayout(this);
        transport.setOrientation(LinearLayout.HORIZONTAL);
        transport.setGravity(Gravity.CENTER);
        transport.setPadding(0, Math.round(dp(20)), 0, Math.round(dp(22)));

        Button btnPrev = makeActionBtn("⏮", Color.argb(36, 255, 255, 255), Color.WHITE);
        btnPrev.setTextSize(18f);
        btnPrev.setBackground(pillDrawable(Color.argb(34, 255, 255, 255), Color.argb(44, 255, 255, 255), 26));
        btnPrev.setOnClickListener(v -> sendAsync(() -> client.sendMediaAction("a=prev")));

        btnPlayPause = makeActionBtn("❚❚", Color.rgb(245, 245, 243), Color.rgb(12, 12, 14));
        btnPlayPause.setTextSize(20f);
        btnPlayPause.setBackground(pillDrawable(Color.rgb(245, 245, 243), Color.TRANSPARENT, 34));
        btnPlayPause.setOnClickListener(v -> {
            // Immediate 0ms optimistic toggle so Play/Pause never feels delayed
            lastState.isPlaying = !lastState.isPlaying;
            btnPlayPause.setText(lastState.isPlaying ? "❚❚" : "▶");
            sendAsync(() -> client.sendMediaAction("a=play"));
        });

        Button btnNext = makeActionBtn("⏭", Color.argb(36, 255, 255, 255), Color.WHITE);
        btnNext.setTextSize(18f);
        btnNext.setBackground(pillDrawable(Color.argb(34, 255, 255, 255), Color.argb(44, 255, 255, 255), 26));
        btnNext.setOnClickListener(v -> sendAsync(() -> client.sendMediaAction("a=next")));

        LinearLayout.LayoutParams cSide1 = new LinearLayout.LayoutParams(Math.round(dp(52)), Math.round(dp(52)));
        cSide1.rightMargin = Math.round(dp(22));
        LinearLayout.LayoutParams cMid = new LinearLayout.LayoutParams(Math.round(dp(68)), Math.round(dp(68)));
        cMid.rightMargin = Math.round(dp(22));
        LinearLayout.LayoutParams cSide2 = new LinearLayout.LayoutParams(Math.round(dp(52)), Math.round(dp(52)));
        transport.addView(btnPrev, cSide1);
        transport.addView(btnPlayPause, cMid);
        transport.addView(btnNext, cSide2);
        tabNow.addView(transport);

        // Sleek bottom volume row (speaker circle + slider + numeric value)
        LinearLayout volRow = new LinearLayout(this);
        volRow.setOrientation(LinearLayout.HORIZONTAL);
        volRow.setGravity(Gravity.CENTER_VERTICAL);
        volRow.setPadding(Math.round(dp(4)), 0, Math.round(dp(4)), Math.round(dp(8)));

        btnMuteVol = makeActionBtn("🔊", Color.argb(34, 255, 255, 255), Color.WHITE);
        btnMuteVol.setTextSize(14f);
        btnMuteVol.setBackground(pillDrawable(Color.argb(34, 255, 255, 255), Color.argb(38, 255, 255, 255), 20));
        btnMuteVol.setPadding(0, 0, 0, 0);
        btnMuteVol.setOnClickListener(v -> {
            lastState.muted = !lastState.muted;
            btnMuteVol.setText(lastState.muted ? "🔇" : "🔊");
            tvVolLabel.setText(lastState.muted ? "0" : String.valueOf(lastState.volume));
            sendAsync(() -> client.sendMediaAction("a=mute"));
        });
        volRow.addView(btnMuteVol, new LinearLayout.LayoutParams(Math.round(dp(38)), Math.round(dp(38))));

        sbVolume = new LiquidSliderView(this);
        sbVolume.setMax(100);
        sbVolume.setOnChangeListener(new LiquidSliderView.OnChangeListener() {
            @Override public void onChanged(int p, boolean fromUser) {
                if (fromUser) {
                    tvVolLabel.setText(String.valueOf(p));
                    long now = System.currentTimeMillis();
                    if (now - lastVolSendMs >= 90L) {
                        lastVolSendMs = now;
                        sendAsync(() -> client.sendMediaAction("a=vol&v=" + p));
                    }
                }
            }
            @Override public void onStartTracking() { isDraggingVolume = true; }
            @Override public void onStopTracking(int p) {
                tvVolLabel.setText(String.valueOf(p));
                sendAsync(() -> client.sendMediaAction("a=vol&v=" + p));
                isDraggingVolume = false;
            }
        });
        LinearLayout.LayoutParams vLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        vLp.leftMargin = Math.round(dp(8));
        vLp.rightMargin = Math.round(dp(8));
        volRow.addView(sbVolume, vLp);

        tvVolLabel = new TextView(this);
        tvVolLabel.setText("--");
        tvVolLabel.setTextSize(13f);
        tvVolLabel.setGravity(Gravity.END);
        volRow.addView(tvVolLabel, new LinearLayout.LayoutParams(Math.round(dp(34)), LinearLayout.LayoutParams.WRAP_CONTENT));
        tabNow.addView(volRow, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        // Hidden mic-mute ref so applyStateToUi keeps working cleanly
        btnMuteMic = makeActionBtn("Mic: Active", Color.argb(30, 255, 255, 255), Color.WHITE);
        btnMuteMic.setOnClickListener(v -> sendAsync(() -> client.sendModeAction("mic")));
        btnMuteMic.setVisibility(View.GONE);
        tabNow.addView(btnMuteMic);

        // Smooth Live Widgets Strip right on the Now screen (interpolated CPU/GPU/RAM + synced Clipboard/Files/To-Do)
        LinearLayout nowStatsRow = new LinearLayout(this);
        nowStatsRow.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams nsLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        nsLp.topMargin = Math.round(dp(14));
        nsLp.bottomMargin = Math.round(dp(10));
        nowCardCpu = new LiquidStatCardView(this, "CPU", Color.rgb(43, 228, 234));
        nowCardGpu = new LiquidStatCardView(this, "GPU", Color.rgb(120, 175, 255));
        nowCardRam = new LiquidStatCardView(this, "RAM", Color.rgb(168, 130, 255));
        LinearLayout.LayoutParams nw1 = new LinearLayout.LayoutParams(0, Math.round(dp(86)), 1f);
        nw1.rightMargin = Math.round(dp(7));
        LinearLayout.LayoutParams nw2 = new LinearLayout.LayoutParams(0, Math.round(dp(86)), 1f);
        nw2.rightMargin = Math.round(dp(7));
        nowStatsRow.addView(nowCardCpu, nw1);
        nowStatsRow.addView(nowCardGpu, nw2);
        nowStatsRow.addView(nowCardRam, new LinearLayout.LayoutParams(0, Math.round(dp(86)), 1f));
        tabNow.addView(nowStatsRow, nsLp);

        LinearLayout widgetDeckCard = makeCardContainer();
        LinearLayout wTabs = new LinearLayout(this);
        wTabs.setOrientation(LinearLayout.HORIZONTAL);
        String[] wLabels = {"📋 Clipboard", "📄 Files", "☑ To-Do"};
        for (int i = 0; i < 3; i++) {
            final int wi = i;
            Button wb = makeActionBtn(wLabels[i], Color.argb(28, 255, 255, 255), Color.WHITE);
            wb.setTextSize(11.5f);
            wb.setPadding(Math.round(dp(6)), Math.round(dp(7)), Math.round(dp(6)), Math.round(dp(7)));
            wb.setOnClickListener(v -> {
                activeNowWidget = wi;
                try { android.transition.TransitionManager.beginDelayedTransition(widgetDeckCard); } catch (Exception ignored) {}
                rebuildNowWidgets(lastState);
            });
            nowWidgetBtns[i] = wb;
            LinearLayout.LayoutParams wLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
            if (i < 2) wLp.rightMargin = Math.round(dp(6));
            wTabs.addView(wb, wLp);
        }
        widgetDeckCard.addView(wTabs);

        nowWidgetContent = new LinearLayout(this);
        nowWidgetContent.setOrientation(LinearLayout.VERTICAL);
        nowWidgetContent.setPadding(0, Math.round(dp(10)), 0, 0);
        widgetDeckCard.addView(nowWidgetContent);
        tabNow.addView(widgetDeckCard);
        rebuildNowWidgets(lastState);
    }

    private void rebuildNowWidgets(PinnedDeckClient.PcState st) {
        if (nowWidgetContent == null || st == null) return;
        for (int i = 0; i < 3; i++) {
            if (nowWidgetBtns[i] == null) continue;
            boolean sel = i == activeNowWidget;
            nowWidgetBtns[i].setBackground(pillDrawable(
                    sel ? activeAccentColor : Color.argb(24, 255, 255, 255), Color.TRANSPARENT, 12));
            nowWidgetBtns[i].setTextColor(sel ? Color.rgb(10, 12, 16) : Color.argb(215, 235, 238, 245));
        }
        nowWidgetContent.removeAllViews();
        if (activeNowWidget == 0) {
            if (st.clipboard.isEmpty()) {
                nowWidgetContent.addView(makeWidgetEmptyRow("No PC clipboard items yet"));
            } else {
                int n = Math.min(4, st.clipboard.size());
                for (int i = 0; i < n; i++) {
                    final PinnedDeckClient.ClipItem ci = st.clipboard.get(i);
                    TextView row = makeWidgetRow((ci.pinned ? "📌  " : "📋  ") + ci.text.replace('\n', ' '), false);
                    row.setOnClickListener(v -> {
                        v.animate().scaleX(0.96f).scaleY(0.96f).setDuration(60)
                                .withEndAction(() -> v.animate().scaleX(1f).scaleY(1f).setDuration(80).start()).start();
                        try {
                            ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
                            if (cm != null) cm.setPrimaryClip(ClipData.newPlainText("NotchPixel", ci.text));
                            Toast.makeText(this, "Copied to phone clipboard", Toast.LENGTH_SHORT).show();
                        } catch (Exception ignored) {}
                    });
                    nowWidgetContent.addView(row);
                }
            }
        } else if (activeNowWidget == 1) {
            if (st.shelf.isEmpty()) {
                nowWidgetContent.addView(makeWidgetEmptyRow("Drop files onto your PC Notch Shelf"));
            } else {
                int n = Math.min(4, st.shelf.size());
                for (int i = 0; i < n; i++) {
                    nowWidgetContent.addView(makeShelfItemView(st.shelf.get(i)));
                }
            }
        } else {
            if (st.todos.isEmpty()) {
                nowWidgetContent.addView(makeWidgetEmptyRow("No PC To-Do tasks yet (add in PC tab)"));
            } else {
                int n = Math.min(5, st.todos.size());
                for (int i = 0; i < n; i++) {
                    final PinnedDeckClient.TodoItem ti = st.todos.get(i);
                    TextView row = makeWidgetRow((ti.done ? "☑  " : "☐  ") + ti.text, ti.done);
                    row.setOnClickListener(v -> {
                        v.animate().scaleX(0.96f).scaleY(0.96f).setDuration(60)
                                .withEndAction(() -> v.animate().scaleX(1f).scaleY(1f).setDuration(80).start()).start();
                        sendAsync(() -> client.toggleTodo(ti.index));
                    });
                    nowWidgetContent.addView(row);
                }
            }
        }
    }

    private View makeShelfItemView(final PinnedDeckClient.ShelfItem sf) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setBackground(pillDrawable(Color.argb(32, 255, 255, 255), Color.argb(28, 255, 255, 255), 14));
        row.setPadding(Math.round(dp(8)), Math.round(dp(8)), Math.round(dp(12)), Math.round(dp(8)));

        int thumbSz = Math.round(dp(44));
        Bitmap thumbBmp = (sf.isImage && sf.thumbB64 != null && !sf.thumbB64.isEmpty())
                ? decodeBase64Bitmap(sf.thumbB64)
                : null;
        if (thumbBmp != null) {
            ImageView iv = new ImageView(this);
            iv.setImageBitmap(thumbBmp);
            iv.setScaleType(ImageView.ScaleType.CENTER_CROP);
            final float rPx = dp(10);
            iv.setClipToOutline(true);
            iv.setOutlineProvider(new ViewOutlineProvider() {
                @Override public void getOutline(View v, Outline o) { o.setRoundRect(0, 0, v.getWidth(), v.getHeight(), rPx); }
            });
            LinearLayout.LayoutParams iLp = new LinearLayout.LayoutParams(thumbSz, thumbSz);
            iLp.rightMargin = Math.round(dp(10));
            row.addView(iv, iLp);
        } else {
            TextView badge = new TextView(this);
            badge.setText(sf.ext == null || sf.ext.isEmpty() ? "FILE" : (sf.ext.length() > 4 ? sf.ext.substring(0, 4) : sf.ext));
            badge.setTextSize(10f);
            badge.setGravity(Gravity.CENTER);
            badge.getPaint().setFakeBoldText(true);
            badge.setTextColor(Color.WHITE);
            badge.setBackground(pillDrawable(Color.argb(65, Color.red(activeAccentColor), Color.green(activeAccentColor), Color.blue(activeAccentColor)),
                    Color.argb(40, 255, 255, 255), 10));
            LinearLayout.LayoutParams bLp = new LinearLayout.LayoutParams(thumbSz, thumbSz);
            bLp.rightMargin = Math.round(dp(10));
            row.addView(badge, bLp);
        }

        LinearLayout meta = new LinearLayout(this);
        meta.setOrientation(LinearLayout.VERTICAL);
        TextView tvName = new TextView(this);
        tvName.setText(sf.name);
        tvName.setTextSize(12.5f);
        tvName.setSingleLine(true);
        tvName.getPaint().setFakeBoldText(true);
        tvName.setTextColor(Color.WHITE);
        meta.addView(tvName);

        long kb = Math.max(1L, sf.sizeBytes / 1024L);
        TextView tvSub = new TextView(this);
        tvSub.setText((sf.isImage ? "Image · " : "") + kb + " KB · Tap to Download or Copy");
        tvSub.setTextSize(10.5f);
        tvSub.setSingleLine(true);
        tvSub.setTextColor(Color.argb(160, 210, 220, 232));
        meta.addView(tvSub);

        row.addView(meta, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        row.setOnClickListener(v -> {
            v.animate().scaleX(0.97f).scaleY(0.97f).setDuration(60)
                    .withEndAction(() -> v.animate().scaleX(1f).scaleY(1f).setDuration(80).start()).start();
            showShelfItemActions(sf, thumbBmp);
        });

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Math.round(dp(7));
        row.setLayoutParams(lp);
        return row;
    }

    private void showShelfItemActions(final PinnedDeckClient.ShelfItem sf, final Bitmap previewBmp) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setGravity(Gravity.CENTER_HORIZONTAL);
        box.setPadding(Math.round(dp(20)), Math.round(dp(14)), Math.round(dp(20)), Math.round(dp(6)));
        if (previewBmp != null) {
            ImageView iv = new ImageView(this);
            iv.setImageBitmap(previewBmp);
            iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
            LinearLayout.LayoutParams iLp = new LinearLayout.LayoutParams(Math.round(dp(180)), Math.round(dp(140)));
            iLp.bottomMargin = Math.round(dp(10));
            box.addView(iv, iLp);
        }
        TextView info = new TextView(this);
        long kb = Math.max(1L, sf.sizeBytes / 1024L);
        info.setText(sf.name + " (" + kb + " KB)");
        info.setTextSize(12.5f);
        box.addView(info);

        new AlertDialog.Builder(this)
                .setTitle("PC Shelf File")
                .setView(box)
                .setPositiveButton("Download", (d, w) -> {
                    Toast.makeText(this, "Downloading " + sf.name + "...", Toast.LENGTH_SHORT).show();
                    cmdExecutor.execute(() -> {
                        try {
                            byte[] bytes = client.downloadShelfFile(sf.index);
                            boolean saved = saveBytesToDownloads(sf.name, bytes, sf.isImage);
                            runOnUiThread(() -> Toast.makeText(this,
                                    saved ? ("Saved to Downloads: " + sf.name) : "Could not save file",
                                    Toast.LENGTH_SHORT).show());
                        } catch (Exception ex) {
                            runOnUiThread(() -> Toast.makeText(this, "Download failed: " + ex.getMessage(), Toast.LENGTH_SHORT).show());
                        }
                    });
                })
                .setNeutralButton("Copy", (d, w) -> {
                    try {
                        ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
                        if (cm != null) cm.setPrimaryClip(ClipData.newPlainText("NotchPixel Shelf", sf.name));
                        Toast.makeText(this, "Copied to clipboard", Toast.LENGTH_SHORT).show();
                    } catch (Exception ignored) {}
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    private boolean saveBytesToDownloads(String fileName, byte[] data, boolean isImage) {
        try {
            if (android.os.Build.VERSION.SDK_INT >= 29) {
                android.content.ContentValues cv = new android.content.ContentValues();
                cv.put(android.provider.MediaStore.Downloads.DISPLAY_NAME, fileName);
                cv.put(android.provider.MediaStore.Downloads.MIME_TYPE, isImage ? "image/*" : "application/octet-stream");
                cv.put(android.provider.MediaStore.Downloads.RELATIVE_PATH, android.os.Environment.DIRECTORY_DOWNLOADS + "/NotchPixel");
                android.net.Uri uri = getContentResolver().insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, cv);
                if (uri == null) return false;
                try (java.io.OutputStream os = getContentResolver().openOutputStream(uri)) {
                    if (os == null) return false;
                    os.write(data);
                }
                return true;
            } else {
                java.io.File dir = new java.io.File(android.os.Environment.getExternalStoragePublicDirectory(
                        android.os.Environment.DIRECTORY_DOWNLOADS), "NotchPixel");
                dir.mkdirs();
                java.io.File out = new java.io.File(dir, fileName);
                try (java.io.FileOutputStream fos = new java.io.FileOutputStream(out)) {
                    fos.write(data);
                }
                return true;
            }
        } catch (Exception ignored) {
            return false;
        }
    }

    private TextView makeWidgetEmptyRow(String msg) {
        TextView tv = new TextView(this);
        tv.setText(msg);
        tv.setTextSize(12f);
        tv.setTextColor(Color.argb(145, 215, 222, 232));
        tv.setPadding(Math.round(dp(4)), Math.round(dp(4)), Math.round(dp(4)), Math.round(dp(4)));
        return tv;
    }

    private TextView makeWidgetRow(String text, boolean done) {
        TextView row = new TextView(this);
        row.setText(text);
        row.setTextSize(12.5f);
        row.setSingleLine(true);
        row.setTextColor(done ? Color.argb(145, 200, 208, 220) : Color.WHITE);
        row.setBackground(pillDrawable(
                done ? Color.argb(24, 48, 209, 88) : Color.argb(28, 255, 255, 255),
                Color.argb(24, 255, 255, 255), 12));
        row.setPadding(Math.round(dp(12)), Math.round(dp(9)), Math.round(dp(12)), Math.round(dp(9)));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Math.round(dp(6));
        row.setLayoutParams(lp);
        return row;
    }

    private void buildTabPad() {
        tabPad = new LinearLayout(this);
        tabPad.setOrientation(LinearLayout.VERTICAL);

        padCanvas = new PadCanvasView(this, new PadCanvasView.PadActionSink() {
            @Override public void onMove(int dx, int dy) {
                pendingPadDx.addAndGet(dx);
                pendingPadDy.addAndGet(dy);
                schedulePadFlush();
            }
            @Override public void onScroll(int dy) {
                pendingPadScroll.addAndGet(dy);
                schedulePadFlush();
            }
            @Override public void onLeftClick() { padUdpExecutor.execute(() -> client.sendPadUdp("lclick")); }
            @Override public void onRightClick() { padUdpExecutor.execute(() -> client.sendPadUdp("rclick")); }
            @Override public void onDragStart() { padUdpExecutor.execute(() -> client.sendPadUdp("drag_start")); }
            @Override public void onDragEnd() { padUdpExecutor.execute(() -> client.sendPadUdp("drag_end")); }
            @Override public void onRelease() {
                // Immediately drop any residual deltas when finger lifts -> zero drift!
                pendingPadDx.set(0);
                pendingPadDy.set(0);
                pendingPadScroll.set(0);
            }
        });
        LinearLayout.LayoutParams padLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        padLp.bottomMargin = Math.round(dp(10));
        tabPad.addView(padCanvas, padLp);

        // Keyboard + Copy/Paste/Undo card
        LinearLayout kbCard = makeCardContainer();
        LinearLayout typeRow = new LinearLayout(this);
        typeRow.setOrientation(LinearLayout.HORIZONTAL);
        etPadType = new EditText(this);
        etPadType.setHint("Type to PC...");
        etPadType.setSingleLine(true);
        etPadType.setTextSize(14f);
        etPadType.setImeOptions(EditorInfo.IME_ACTION_SEND);
        etPadType.setOnEditorActionListener((v, actionId, event) -> {
            if (actionId == EditorInfo.IME_ACTION_SEND || actionId == EditorInfo.IME_ACTION_DONE) {
                sendPadTypedText();
                return true;
            }
            return false;
        });
        typeRow.addView(etPadType, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        Button btnSendType = makeActionBtn("Send", Color.rgb(43, 228, 234), Color.rgb(8, 12, 16));
        btnSendType.setOnClickListener(v -> sendPadTypedText());
        typeRow.addView(btnSendType);
        kbCard.addView(typeRow);

        LinearLayout clipRow = new LinearLayout(this);
        clipRow.setOrientation(LinearLayout.HORIZONTAL);
        clipRow.setPadding(0, Math.round(dp(8)), 0, Math.round(dp(10)));
        String[][] clips = {{"Copy", "copy"}, {"Paste", "paste"}, {"Undo", "undo"}};
        for (int i = 0; i < clips.length; i++) {
            final String act = clips[i][1];
            Button b = makeActionBtn(clips[i][0], Color.rgb(32, 37, 47), Color.WHITE);
            b.setOnClickListener(v -> sendAsync(() -> client.sendPadAction("a=" + act)));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
            if (i < clips.length - 1) lp.rightMargin = Math.round(dp(6));
            clipRow.addView(b, lp);
        }
        kbCard.addView(clipRow);

        // Pointer & Scroll speeds + Trail/Haptics
        String[] speedNames = {"Slow", "Normal", "Fast", "Faster"};
        TextView tvPs = new TextView(this);
        tvPs.setText("Pointer speed");
        tvPs.setTextSize(12f);
        kbCard.addView(tvPs);
        LinearLayout psRow = new LinearLayout(this);
        psRow.setOrientation(LinearLayout.HORIZONTAL);
        for (int i = 0; i < 4; i++) {
            final int idx = i;
            Button b = makeActionBtn(speedNames[i], Color.rgb(32, 37, 47), Color.WHITE);
            b.setTextSize(11.5f);
            b.setOnClickListener(v -> { store.setPadSpeed(idx); refreshPadPrefs(); });
            padSpeedBtns[i] = b;
            psRow.addView(b, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        }
        kbCard.addView(psRow);

        TextView tvSs = new TextView(this);
        tvSs.setText("Two-finger scroll speed");
        tvSs.setTextSize(12f);
        tvSs.setPadding(0, Math.round(dp(8)), 0, 0);
        kbCard.addView(tvSs);
        LinearLayout ssRow = new LinearLayout(this);
        ssRow.setOrientation(LinearLayout.HORIZONTAL);
        for (int i = 0; i < 4; i++) {
            final int idx = i;
            Button b = makeActionBtn(speedNames[i], Color.rgb(32, 37, 47), Color.WHITE);
            b.setTextSize(11.5f);
            b.setOnClickListener(v -> { store.setScrollSpeed(idx); refreshPadPrefs(); });
            scrollSpeedBtns[i] = b;
            ssRow.addView(b, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        }
        kbCard.addView(ssRow);

        swTrailToggle = new LiquidToggleView(this);
        swTrailToggle.setChecked(store.isTrailEnabled());
        swTrailToggle.setOnCheckedChangeListener(checked -> { store.setTrailEnabled(checked); refreshPadPrefs(); });
        kbCard.addView(makeLiquidToggleRow("Dot-matrix pointer trail", swTrailToggle));

        swHapticsToggle = new LiquidToggleView(this);
        swHapticsToggle.setChecked(store.isHapticsEnabled());
        swHapticsToggle.setOnCheckedChangeListener(checked -> { store.setHapticsEnabled(checked); refreshPadPrefs(); });
        kbCard.addView(makeLiquidToggleRow("Subtle touch haptics", swHapticsToggle));

        tabPad.addView(kbCard);
        refreshPadPrefs();
    }

    private void sendPadTypedText() {
        final String txt = etPadType.getText().toString();
        if (!txt.isEmpty()) {
            etPadType.setText("");
            sendAsync(() -> client.sendPadAction("a=type&text=" + URLEncoder.encode(txt, "UTF-8")));
        }
    }

    private void refreshPadPrefs() {
        padCanvas.configure(store.getPadSpeed(), store.getScrollSpeed(),
                store.isTrailEnabled(), store.isHapticsEnabled(), lightSurface);
        for (int i = 0; i < 4; i++) {
            boolean pSel = store.getPadSpeed() == i;
            padSpeedBtns[i].setBackground(pillDrawable(
                    pSel ? Color.rgb(43, 228, 234) : Color.rgb(32, 37, 47), Color.TRANSPARENT, 12));
            padSpeedBtns[i].setTextColor(pSel ? Color.rgb(8, 12, 16) : Color.WHITE);

            boolean sSel = store.getScrollSpeed() == i;
            scrollSpeedBtns[i].setBackground(pillDrawable(
                    sSel ? Color.rgb(43, 228, 234) : Color.rgb(32, 37, 47), Color.TRANSPARENT, 12));
            scrollSpeedBtns[i].setTextColor(sSel ? Color.rgb(8, 12, 16) : Color.WHITE);
        }
    }

    private void buildTabApps() {
        tabApps = new LinearLayout(this);
        tabApps.setOrientation(LinearLayout.VERTICAL);

        LinearLayout card = makeCardContainer();
        appsCardHeader = new LinearLayout(this);
        appsCardHeader.setOrientation(LinearLayout.HORIZONTAL);
        appsCardHeader.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams hLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        hLp.bottomMargin = Math.round(dp(12));
        appsCardHeader.setLayoutParams(hLp);

        TextView hdr = new TextView(this);
        hdr.setText("Stream Deck");
        hdr.setTextSize(15f);
        hdr.setTextColor(Color.WHITE);
        hdr.getPaint().setFakeBoldText(true);
        appsCardHeader.addView(hdr, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        btnDeckFullscreen = makeActionBtn("⛶ Landscape Deck", Color.argb(44, 255, 255, 255), Color.WHITE);
        btnDeckFullscreen.setTextSize(11.5f);
        btnDeckFullscreen.setPadding(Math.round(dp(12)), Math.round(dp(7)), Math.round(dp(12)), Math.round(dp(7)));
        btnDeckFullscreen.setOnClickListener(v -> toggleDeckFullscreen());
        appsCardHeader.addView(btnDeckFullscreen);
        card.addView(appsCardHeader);

        // Split container: Left = compact Stream Deck grid, Right = Landscape control sidebar (visible in Fullscreen)
        deckSplitRow = new LinearLayout(this);
        deckSplitRow.setOrientation(LinearLayout.HORIZONTAL);
        deckSplitRow.setGravity(Gravity.CENTER_VERTICAL);

        appsGrid = new GridLayout(this);
        appsGrid.setColumnCount(3);
        appsGrid.setUseDefaultMargins(false);
        deckSplitRow.addView(appsGrid, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        deckSidebar = new LinearLayout(this);
        deckSidebar.setOrientation(LinearLayout.VERTICAL);
        deckSidebar.setVisibility(View.GONE);
        deckSidebar.setPadding(Math.round(dp(10)), 0, 0, 0);

        btnSideAudioMute = makeActionBtn("🔊 Mute Audio", Color.argb(56, 245, 166, 35), Color.WHITE);
        btnSideAudioMute.setTextSize(11.5f);
        btnSideAudioMute.setOnClickListener(v -> sendAsync(() -> client.sendMediaAction("a=mute")));

        btnSideMicMute = makeActionBtn("🎙 Mute Mic", Color.argb(62, 235, 72, 88), Color.WHITE);
        btnSideMicMute.setTextSize(11.5f);
        btnSideMicMute.setOnClickListener(v -> sendAsync(() -> client.sendModeAction("mic")));

        btnSideTestAudio = makeActionBtn("♪ Test Audio", Color.argb(56, 56, 178, 242), Color.WHITE);
        btnSideTestAudio.setTextSize(11.5f);
        btnSideTestAudio.setOnClickListener(v -> sendAsync(() -> client.testAudio()));

        Button btnSideExit = makeActionBtn("✕ Exit Deck", Color.argb(48, 255, 255, 255), Color.WHITE);
        btnSideExit.setTextSize(11.5f);
        btnSideExit.setOnClickListener(v -> toggleDeckFullscreen());

        LinearLayout.LayoutParams sbLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, Math.round(dp(44)));
        sbLp.bottomMargin = Math.round(dp(7));
        deckSidebar.addView(btnSideAudioMute, new LinearLayout.LayoutParams(sbLp));
        deckSidebar.addView(btnSideMicMute, new LinearLayout.LayoutParams(sbLp));
        deckSidebar.addView(btnSideTestAudio, new LinearLayout.LayoutParams(sbLp));
        deckSidebar.addView(btnSideExit, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, Math.round(dp(44))));

        deckSplitRow.addView(deckSidebar, new LinearLayout.LayoutParams(
                Math.round(dp(138)), LinearLayout.LayoutParams.WRAP_CONTENT));
        card.addView(deckSplitRow);

        btnAddSlotRef = makeActionBtn("+ Pin App to Deck", activeAccentColor, Color.rgb(8, 12, 16));
        btnAddSlotRef.setOnClickListener(v -> showAddAppDialog());
        LinearLayout.LayoutParams aLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        aLp.topMargin = Math.round(dp(14));
        card.addView(btnAddSlotRef, aLp);

        tabApps.addView(card);
        rebuildAppsGrid();
    }

    private void toggleDeckFullscreen() {
        deckFullscreen = !deckFullscreen;
        topPillWrap.setVisibility(deckFullscreen ? View.GONE : View.VISIBLE);
        bottomTabBar.setVisibility(deckFullscreen ? View.GONE : View.VISIBLE);
        if (appsCardHeader != null) appsCardHeader.setVisibility(deckFullscreen ? View.GONE : View.VISIBLE);
        if (btnAddSlotRef != null) btnAddSlotRef.setVisibility(deckFullscreen ? View.GONE : View.VISIBLE);
        if (deckSidebar != null) deckSidebar.setVisibility(deckFullscreen ? View.VISIBLE : View.GONE);
        rootLayout.setPadding(
                Math.round(dp(deckFullscreen ? 8 : 14)),
                Math.round(dp(deckFullscreen ? 6 : 18)),
                Math.round(dp(deckFullscreen ? 8 : 14)),
                Math.round(dp(deckFullscreen ? 6 : 14)));
        try {
            setRequestedOrientation(deckFullscreen
                    ? android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                    : android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
            View decor = getWindow().getDecorView();
            if (deckFullscreen) {
                decor.setSystemUiVisibility(
                        View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                                | View.SYSTEM_UI_FLAG_FULLSCREEN
                                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION);
            } else {
                decor.setSystemUiVisibility(View.SYSTEM_UI_FLAG_VISIBLE);
            }
        } catch (Exception ignored) {}
        rebuildAppsGrid();
    }

    private static Bitmap decodeBase64Bitmap(String b64) {
        if (b64 == null || b64.isEmpty()) return null;
        try {
            byte[] raw = Base64.decode(b64, Base64.DEFAULT);
            return BitmapFactory.decodeByteArray(raw, 0, raw.length);
        } catch (Exception ignored) { return null; }
    }

    private View makeStreamDeckKey(String iconGlyph, String iconB64, String label, int badgeColor, boolean configured, Runnable onTap) {
        LinearLayout key = new LinearLayout(this);
        key.setOrientation(LinearLayout.VERTICAL);
        key.setGravity(Gravity.CENTER);
        key.setPadding(Math.round(dp(6)), Math.round(dp(10)), Math.round(dp(6)), Math.round(dp(10)));
        int fill = configured
                ? (lightSurface ? Color.argb(225, 255, 255, 255) : Color.argb(50, 255, 255, 255))
                : (lightSurface ? Color.argb(95, 240, 242, 246) : Color.argb(20, 255, 255, 255));
        int stroke = configured
                ? Color.argb(110, Color.red(badgeColor), Color.green(badgeColor), Color.blue(badgeColor))
                : (lightSurface ? Color.argb(30, 12, 12, 14) : Color.argb(32, 255, 255, 255));
        key.setBackground(pillDrawable(fill, stroke, 18));

        int iconSz = Math.round(dp(deckFullscreen ? 36 : 44));
        Bitmap customIcon = configured ? decodeBase64Bitmap(iconB64) : null;
        if (customIcon != null) {
            ImageView iv = new ImageView(this);
            iv.setImageBitmap(customIcon);
            iv.setScaleType(ImageView.ScaleType.CENTER_CROP);
            final float rPx = dp(10);
            iv.setClipToOutline(true);
            iv.setOutlineProvider(new ViewOutlineProvider() {
                @Override public void getOutline(View v, Outline o) { o.setRoundRect(0, 0, v.getWidth(), v.getHeight(), rPx); }
            });
            LinearLayout.LayoutParams bLp = new LinearLayout.LayoutParams(iconSz, iconSz);
            bLp.bottomMargin = Math.round(dp(deckFullscreen ? 4 : 7));
            key.addView(iv, bLp);
        } else {
            TextView badge = new TextView(this);
            badge.setText(iconGlyph);
            badge.setGravity(Gravity.CENTER);
            badge.setTextSize(deckFullscreen ? 16f : 19f);
            badge.setTextColor(Color.WHITE);
            badge.setBackground(pillDrawable(
                    configured ? badgeColor : Color.argb(40, 140, 150, 165),
                    Color.argb(65, 255, 255, 255), 12));
            LinearLayout.LayoutParams bLp = new LinearLayout.LayoutParams(iconSz, iconSz);
            bLp.bottomMargin = Math.round(dp(deckFullscreen ? 4 : 7));
            key.addView(badge, bLp);
        }

        TextView tv = new TextView(this);
        tv.setText(label);
        tv.setTextSize(deckFullscreen ? 10.5f : 11f);
        tv.setSingleLine(true);
        tv.setGravity(Gravity.CENTER);
        tv.getPaint().setFakeBoldText(configured);
        tv.setTextColor(configured
                ? (lightSurface ? Color.rgb(16, 18, 22) : Color.WHITE)
                : (lightSurface ? Color.rgb(110, 115, 125) : Color.argb(145, 210, 218, 228)));
        key.addView(tv);

        key.setOnClickListener(v -> {
            v.animate().scaleX(0.91f).scaleY(0.91f).setDuration(60)
                    .withEndAction(() -> v.animate().scaleX(1f).scaleY(1f).setDuration(90).start())
                    .start();
            onTap.run();
        });

        GridLayout.LayoutParams glp = new GridLayout.LayoutParams();
        glp.width = 0;
        glp.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
        glp.height = Math.round(dp(deckFullscreen ? 88 : 100));
        int m = Math.round(dp(deckFullscreen ? 4 : 5));
        glp.setMargins(m, m, m, m);
        key.setLayoutParams(glp);
        return key;
    }

    private void rebuildAppsGrid() {
        appsGrid.removeAllViews();
        int[] palette = {
                Color.rgb(43, 180, 234), Color.rgb(122, 92, 245), Color.rgb(48, 209, 88),
                Color.rgb(255, 149, 0), Color.rgb(255, 69, 98), Color.rgb(10, 132, 255)
        };
        String[] glyphs = {"⌘", "⚡", "▶", "✦", "◈", "◎"};
        for (int i = 0; i < 6; i++) {
            final int slotIdx = i;
            PinnedDeckClient.AppSlot slot = i < lastState.apps.size() ? lastState.apps.get(i) : null;
            boolean cfg = slot != null && slot.configured;
            String label = cfg ? slot.label : ("Slot " + (i + 1));
            String glyph = cfg && label.length() > 0 ? label.substring(0, 1).toUpperCase(Locale.US) : glyphs[i];
            String iconB64 = cfg ? slot.iconB64 : "";
            appsGrid.addView(makeStreamDeckKey(glyph, iconB64, label, palette[i % palette.length], cfg, () -> {
                if (cfg) sendAsync(() -> client.launchAppSlot(slotIdx));
                else showAddAppDialog();
            }));
        }
        // In portrait mode show built-in quick actions row; in landscape fullscreen they live in the right sidebar (zero scroll!)
        if (!deckFullscreen) {
            appsGrid.addView(makeStreamDeckKey("🎙", "", lastState.micMuted ? "Unmute Mic" : "Mute Mic",
                    Color.rgb(235, 72, 88), true, () -> sendAsync(() -> client.sendModeAction("mic"))));
            appsGrid.addView(makeStreamDeckKey("📸", "", "Screenshot",
                    Color.rgb(56, 178, 242), true, () -> sendAsync(() -> client.sendModeAction("screenshot"))));
            appsGrid.addView(makeStreamDeckKey("🔊", "", lastState.muted ? "Unmute PC" : "Mute PC",
                    Color.rgb(245, 166, 35), true, () -> sendAsync(() -> client.sendMediaAction("a=mute"))));
        }
        if (btnSideAudioMute != null) {
            btnSideAudioMute.setText(lastState.muted ? "🔇 Unmute PC" : "🔊 Mute Audio");
        }
        if (btnSideMicMute != null) {
            btnSideMicMute.setText(lastState.micMuted ? "🎙 Unmute Mic" : "🎙 Mute Mic");
        }
    }

    private void showAddAppDialog() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(Math.round(dp(18)), Math.round(dp(12)), Math.round(dp(18)), Math.round(dp(6)));
        final EditText etLabel = new EditText(this);
        etLabel.setHint("Display Name (e.g. VS Code)");
        final EditText etPath = new EditText(this);
        etPath.setHint("Executable or URL (e.g. code or notepad.exe)");
        box.addView(etLabel);
        box.addView(etPath);
        new AlertDialog.Builder(this)
                .setTitle("Pin App to PC Launcher")
                .setView(box)
                .setPositiveButton("Add", (d, w) -> {
                    final String l = etLabel.getText().toString().trim();
                    final String p = etPath.getText().toString().trim();
                    if (!p.isEmpty()) sendAsync(() -> client.addAppSlot(l, p));
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    private void buildTabModes() {
        tabModes = new LinearLayout(this);
        tabModes.setOrientation(LinearLayout.VERTICAL);

        LinearLayout seg = new LinearLayout(this);
        seg.setOrientation(LinearLayout.HORIZONTAL);
        String[] modes = {"Game", "Present", "Movie", "Work"};
        for (int i = 0; i < 4; i++) {
            final int idx = i;
            Button b = makeActionBtn(modes[i], Color.rgb(30, 35, 44), Color.WHITE);
            b.setOnClickListener(v -> switchModeSub(idx));
            modeSubBtns[i] = b;
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
            if (i < 3) lp.rightMargin = Math.round(dp(6));
            seg.addView(b, lp);
        }
        LinearLayout.LayoutParams segLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        segLp.bottomMargin = Math.round(dp(12));
        tabModes.addView(seg, segLp);

        modeSubHost = new FrameLayout(this);
        tabModes.addView(modeSubHost);
        switchModeSub(0);
    }

    private void switchModeSub(int idx) {
        activeModeSub = idx;
        for (int i = 0; i < 4; i++) {
            boolean sel = i == idx;
            modeSubBtns[i].setBackground(pillDrawable(
                    sel ? Color.rgb(43, 228, 234) : Color.rgb(30, 35, 44), Color.TRANSPARENT, 14));
            modeSubBtns[i].setTextColor(sel ? Color.rgb(8, 12, 16) : Color.WHITE);
        }
        modeSubHost.removeAllViews();
        btnGameRecord = null;
        LinearLayout card = makeCardContainer();

        if (idx == 0) {
            // Game mode 3x2 deck
            tvRecTimer = new TextView(this);
            tvRecTimer.setText(lastState.recording ? ("Recording  " + fmtSec(lastState.recordingSec)) : "Game Deck Ready");
            tvRecTimer.setTextColor(lastState.recording ? Color.rgb(242, 72, 72) : Color.rgb(43, 228, 234));
            tvRecTimer.getPaint().setFakeBoldText(true);
            tvRecTimer.setPadding(0, 0, 0, Math.round(dp(10)));
            card.addView(tvRecTimer);

            GridLayout grid = new GridLayout(this);
            grid.setColumnCount(3);
            grid.setUseDefaultMargins(true);
            String[][] items = {
                    {"Game Bar", "gamebar"},
                    {"Screenshot", "screenshot"},
                    {"Save Clip", "clip"},
                    {"Record / Stop", "record"},
                    {"Switch", "switch"},
                    {"Mic Mute", "mic"}
            };
            for (final String[] it : items) {
                Button b = makeActionBtn(it[0],
                        "record".equals(it[1]) && lastState.recording ? Color.rgb(210, 48, 48) : Color.rgb(34, 40, 50),
                        Color.WHITE);
                if ("record".equals(it[1])) btnGameRecord = b;
                b.setOnClickListener(v -> sendAsync(() -> client.sendModeAction(it[1])));
                GridLayout.LayoutParams glp = new GridLayout.LayoutParams();
                glp.width = 0;
                glp.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
                glp.height = Math.round(dp(64));
                grid.addView(b, glp);
            }
            card.addView(grid);
        } else if (idx == 1) {
            // Present / Slideshow
            tvPresentTimer = new TextView(this);
            long el = presentingActive ? (System.currentTimeMillis() - presentStartMs) / 1000L : 0L;
            tvPresentTimer.setText("Presentation Timer: " + fmtSec((int) el));
            tvPresentTimer.setTextColor(Color.rgb(43, 228, 234));
            tvPresentTimer.getPaint().setFakeBoldText(true);
            tvPresentTimer.setPadding(0, 0, 0, Math.round(dp(10)));
            card.addView(tvPresentTimer);

            GridLayout grid = new GridLayout(this);
            grid.setColumnCount(2);
            grid.setUseDefaultMargins(true);
            String[][] items = {
                    {"Start Slides (F5)", "slide_start"},
                    {"End (Esc)", "slide_end"},
                    {"Back", "slide_prev"},
                    {"Next", "slide_next"},
                    {"Black Screen (B)", "slide_black"}
            };
            for (final String[] it : items) {
                Button b = makeActionBtn(it[0], Color.rgb(34, 40, 50), Color.WHITE);
                b.setOnClickListener(v -> {
                    if ("slide_start".equals(it[1])) {
                        presentingActive = true;
                        presentStartMs = System.currentTimeMillis();
                    } else if ("slide_end".equals(it[1])) {
                        presentingActive = false;
                    }
                    sendAsync(() -> client.sendModeAction(it[1]));
                });
                GridLayout.LayoutParams glp = new GridLayout.LayoutParams();
                glp.width = 0;
                glp.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
                glp.height = Math.round(dp(62));
                grid.addView(b, glp);
            }
            card.addView(grid);
        } else if (idx == 2) {
            // Movie mode
            GridLayout grid = new GridLayout(this);
            grid.setColumnCount(2);
            grid.setUseDefaultMargins(true);
            String[][] items = {
                    {"Play / Pause", "movie_play"},
                    {"Fullscreen", "movie_full"},
                    {"Skip Back 10s", "movie_back"},
                    {"Skip Fwd 10s", "movie_fwd"},
                    {"Mute Audio", "movie_mute"}
            };
            for (final String[] it : items) {
                Button b = makeActionBtn(it[0], Color.rgb(34, 40, 50), Color.WHITE);
                b.setOnClickListener(v -> sendAsync(() -> client.sendModeAction(it[1])));
                GridLayout.LayoutParams glp = new GridLayout.LayoutParams();
                glp.width = 0;
                glp.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
                glp.height = Math.round(dp(62));
                grid.addView(b, glp);
            }
            card.addView(grid);
        } else {
            // Work mode
            GridLayout grid = new GridLayout(this);
            grid.setColumnCount(2);
            grid.setUseDefaultMargins(true);
            String[][] items = {
                    {"Copy (Ctrl+C)", "work_copy"},
                    {"Paste (Ctrl+V)", "work_paste"},
                    {"Undo (Ctrl+Z)", "work_undo"},
                    {"Screenshot", "screenshot"},
                    {"Mic Mute", "mic"}
            };
            for (final String[] it : items) {
                Button b = makeActionBtn(it[0], Color.rgb(34, 40, 50), Color.WHITE);
                b.setOnClickListener(v -> sendAsync(() -> client.sendModeAction(it[1])));
                GridLayout.LayoutParams glp = new GridLayout.LayoutParams();
                glp.width = 0;
                glp.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
                glp.height = Math.round(dp(62));
                grid.addView(b, glp);
            }
            card.addView(grid);
        }
        modeSubHost.addView(card);
    }

    private void buildTabPc() {
        tabPc = new LinearLayout(this);
        tabPc.setOrientation(LinearLayout.VERTICAL);

        // 3 Liquid-fill stat cards
        LinearLayout statRow = new LinearLayout(this);
        statRow.setOrientation(LinearLayout.HORIZONTAL);
        cardCpu = new LiquidStatCardView(this, "CPU", Color.rgb(43, 228, 234));
        cardGpu = new LiquidStatCardView(this, "GPU", Color.rgb(120, 175, 255));
        cardRam = new LiquidStatCardView(this, "RAM", Color.rgb(168, 130, 255));
        LinearLayout.LayoutParams sLp1 = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        sLp1.rightMargin = Math.round(dp(8));
        LinearLayout.LayoutParams sLp2 = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        sLp2.rightMargin = Math.round(dp(8));
        statRow.addView(cardCpu, sLp1);
        statRow.addView(cardGpu, sLp2);
        statRow.addView(cardRam, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        LinearLayout.LayoutParams rowLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        rowLp.bottomMargin = Math.round(dp(12));
        tabPc.addView(statRow, rowLp);

        // Power & Wake card
        LinearLayout powerCard = makeCardContainer();
        TextView pTitle = new TextView(this);
        pTitle.setText("PC Power & Wake-on-LAN");
        pTitle.setTextSize(14f);
        pTitle.getPaint().setFakeBoldText(true);
        powerCard.addView(pTitle);

        TextView pWarn = new TextView(this);
        pWarn.setText("Restart & Shut down wait 30 seconds on Windows so you have time to save open work.");
        pWarn.setTextSize(11.5f);
        pWarn.setPadding(0, Math.round(dp(3)), 0, Math.round(dp(10)));
        powerCard.addView(pWarn);

        LinearLayout quickPower = new LinearLayout(this);
        quickPower.setOrientation(LinearLayout.HORIZONTAL);
        Button btnLock = makeActionBtn("Lock PC", Color.rgb(34, 41, 52), Color.WHITE);
        btnLock.setOnClickListener(v -> sendAsync(() -> client.sendPowerAction("lock")));
        Button btnWake = makeActionBtn("Wake PC", Color.rgb(26, 62, 70), Color.rgb(43, 228, 234));
        btnWake.setOnClickListener(v -> cmdExecutor.execute(() -> {
            boolean udpOk = client.sendWakeUdp();
            boolean wolOk = WakeOnLanSender.sendMagicPacket(store.getMacAddress());
            boolean ok = udpOk || wolOk;
            runOnUiThread(() -> Toast.makeText(this,
                    ok ? "Wake signal sent to PC" : "Pair with your PC first",
                    Toast.LENGTH_SHORT).show());
        }));
        LinearLayout.LayoutParams qLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        qLp.rightMargin = Math.round(dp(8));
        quickPower.addView(btnLock, qLp);
        quickPower.addView(btnWake, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        powerCard.addView(quickPower);

        LinearLayout holdRow = new LinearLayout(this);
        holdRow.setOrientation(LinearLayout.HORIZONTAL);
        holdRow.setPadding(0, Math.round(dp(10)), 0, 0);
        btnSleep = new HoldPowerButton(this, "Sleep", Color.rgb(75, 140, 240), () -> sendAsync(() -> client.sendPowerAction("sleep")));
        btnRestart = new HoldPowerButton(this, "Restart", Color.rgb(235, 165, 50), () -> sendAsync(() -> client.sendPowerAction("restart")));
        btnShutdown = new HoldPowerButton(this, "Shut down", Color.rgb(235, 72, 72), () -> sendAsync(() -> client.sendPowerAction("shutdown")));
        LinearLayout.LayoutParams hLp1 = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        hLp1.rightMargin = Math.round(dp(6));
        LinearLayout.LayoutParams hLp2 = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        hLp2.rightMargin = Math.round(dp(6));
        holdRow.addView(btnSleep, hLp1);
        holdRow.addView(btnRestart, hLp2);
        holdRow.addView(btnShutdown, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        powerCard.addView(holdRow);
        tabPc.addView(powerCard);

        // Live PC Shelf & Clipboard card (directly accessible on phone whenever connected)
        LinearLayout syncCard = makeCardContainer();
        TextView tvShelfHdr = new TextView(this);
        tvShelfHdr.setText("PC Shelf");
        tvShelfHdr.setTextSize(14f);
        tvShelfHdr.setTextColor(Color.WHITE);
        tvShelfHdr.getPaint().setFakeBoldText(true);
        tvShelfHdr.setPadding(0, 0, 0, Math.round(dp(6)));
        syncCard.addView(tvShelfHdr);

        shelfListBox = new LinearLayout(this);
        shelfListBox.setOrientation(LinearLayout.VERTICAL);
        syncCard.addView(shelfListBox);

        TextView tvClipHdr = new TextView(this);
        tvClipHdr.setText("PC Clipboard (tap to copy to phone)");
        tvClipHdr.setTextSize(14f);
        tvClipHdr.setTextColor(Color.WHITE);
        tvClipHdr.getPaint().setFakeBoldText(true);
        tvClipHdr.setPadding(0, Math.round(dp(12)), 0, Math.round(dp(6)));
        syncCard.addView(tvClipHdr);

        clipListBox = new LinearLayout(this);
        clipListBox.setOrientation(LinearLayout.VERTICAL);
        syncCard.addView(clipListBox);

        // Synced PC To-Do Widget (interactive toggle + quick add)
        LinearLayout todoHdrRow = new LinearLayout(this);
        todoHdrRow.setOrientation(LinearLayout.HORIZONTAL);
        todoHdrRow.setGravity(Gravity.CENTER_VERTICAL);
        todoHdrRow.setPadding(0, Math.round(dp(12)), 0, Math.round(dp(6)));
        TextView tvTodoHdr = new TextView(this);
        tvTodoHdr.setText("PC To-Do (synced)");
        tvTodoHdr.setTextSize(14f);
        tvTodoHdr.setTextColor(Color.WHITE);
        tvTodoHdr.getPaint().setFakeBoldText(true);
        todoHdrRow.addView(tvTodoHdr, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        Button btnAddTodo = makeActionBtn("+ Add", Color.argb(46, 255, 255, 255), Color.WHITE);
        btnAddTodo.setTextSize(11f);
        btnAddTodo.setPadding(Math.round(dp(10)), Math.round(dp(5)), Math.round(dp(10)), Math.round(dp(5)));
        btnAddTodo.setOnClickListener(v -> {
            EditText et = new EditText(this);
            et.setHint("New task for PC To-Do...");
            new AlertDialog.Builder(this)
                    .setTitle("Add PC To-Do")
                    .setView(et)
                    .setPositiveButton("Add", (d, w) -> {
                        String txt = et.getText().toString().trim();
                        if (!txt.isEmpty()) sendAsync(() -> client.addTodo(txt));
                    })
                    .setNegativeButton("Cancel", null)
                    .show();
        });
        todoHdrRow.addView(btnAddTodo);
        syncCard.addView(todoHdrRow);

        todoListBox = new LinearLayout(this);
        todoListBox.setOrientation(LinearLayout.VERTICAL);
        syncCard.addView(todoListBox);
        tabPc.addView(syncCard);

        // Look & Personalization & Pairing card
        LinearLayout prefCard = makeCardContainer();
        TextView tvLook = new TextView(this);
        tvLook.setText("Look");
        tvLook.setTextSize(14f);
        tvLook.getPaint().setFakeBoldText(true);
        prefCard.addView(tvLook);

        LinearLayout lookRow = new LinearLayout(this);
        lookRow.setOrientation(LinearLayout.HORIZONTAL);
        lookRow.setPadding(0, Math.round(dp(6)), 0, Math.round(dp(10)));
        String[] looks = {"Frosted", "Auto", "Dark", "Light"};
        for (int i = 0; i < 4; i++) {
            final String lm = looks[i];
            Button b = makeActionBtn(lm, Color.rgb(32, 37, 47), Color.WHITE);
            b.setTextSize(12f);
            b.setOnClickListener(v -> { store.setLookMode(lm); applyLookTheme(); });
            lookButtons[i] = b;
            LinearLayout.LayoutParams lLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
            if (i < 3) lLp.rightMargin = Math.round(dp(5));
            lookRow.addView(b, lLp);
        }
        prefCard.addView(lookRow);

        TextView tvGrad = new TextView(this);
        tvGrad.setText("Background Gradient");
        tvGrad.setTextSize(13f);
        tvGrad.getPaint().setFakeBoldText(true);
        prefCard.addView(tvGrad);

        LinearLayout gradRow = new LinearLayout(this);
        gradRow.setOrientation(LinearLayout.HORIZONTAL);
        gradRow.setPadding(0, Math.round(dp(6)), 0, Math.round(dp(10)));
        String[] grads = {"None", "Midnight", "Crimson", "Aurora", "Emerald"};
        for (int i = 0; i < 5; i++) {
            final String gp = grads[i];
            Button b = makeActionBtn(gp, Color.rgb(32, 37, 47), Color.WHITE);
            b.setTextSize(10.5f);
            b.setPadding(Math.round(dp(2)), Math.round(dp(8)), Math.round(dp(2)), Math.round(dp(8)));
            b.setOnClickListener(v -> { store.setGradientPreset(gp); applyLookTheme(); });
            gradientButtons[i] = b;
            LinearLayout.LayoutParams gLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
            if (i < 4) gLp.rightMargin = Math.round(dp(4));
            gradRow.addView(b, gLp);
        }
        prefCard.addView(gradRow);

        swMediaNotif = new LiquidToggleView(this);
        swMediaNotif.setChecked(store.isMediaNotifEnabled());
        swMediaNotif.setOnCheckedChangeListener(on -> {
            store.setMediaNotifEnabled(on);
            if (!on) DeckMediaNotificationService.stopService(this);
        });
        prefCard.addView(makeLiquidToggleRow("Lock-screen media notification", swMediaNotif));

        swAudioWave = new LiquidToggleView(this);
        swAudioWave.setChecked(store.isAudioWaveEnabled());
        swAudioWave.setOnCheckedChangeListener(on -> {
            store.setAudioWaveEnabled(on);
            updateAudioWaveVisibility();
        });
        prefCard.addView(makeLiquidToggleRow("Smooth audio wave backdrop (Now)", swAudioWave));

        swSyncMusicColor = new LiquidToggleView(this);
        swSyncMusicColor.setChecked(store.isSyncMusicColorEnabled());
        swSyncMusicColor.setOnCheckedChangeListener(on -> {
            store.setSyncMusicColorEnabled(on);
            applyLookTheme();
        });
        prefCard.addView(makeLiquidToggleRow("Reflect playing music color", swSyncMusicColor));

        tvPairedSummary = new TextView(this);
        tvPairedSummary.setTextSize(12f);
        tvPairedSummary.setPadding(0, Math.round(dp(10)), 0, Math.round(dp(8)));
        prefCard.addView(tvPairedSummary);

        tvPcTabNotice = new TextView(this);
        tvPcTabNotice.setTextSize(12f);
        tvPcTabNotice.setTextColor(Color.rgb(43, 228, 234));
        prefCard.addView(tvPcTabNotice);

        Button btnForget = makeActionBtn("Forget this PC on this phone", Color.rgb(75, 28, 32), Color.rgb(255, 200, 200));
        btnForget.setOnClickListener(v -> new AlertDialog.Builder(this)
                .setTitle("Forget paired PC?")
                .setMessage("Notifies the PC to unpair this device and removes encrypted Keystore token and pinned PC certificate fingerprint from this phone.")
                .setPositiveButton("Forget PC", (d, w) -> {
                    tvPairedSummary.setText("Unpairing from PC...");
                    cmdExecutor.execute(() -> {
                        client.unpairWithPc();
                        runOnUiThread(() -> {
                            store.forgetPc();
                            deviceRevoked = false;
                            DeckMediaNotificationService.stopService(MainActivity.this);
                            applyDisconnectedState("Paired PC forgotten on this phone.");
                        });
                    });
                })
                .setNegativeButton("Cancel", null)
                .show());
        LinearLayout.LayoutParams fgLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        fgLp.topMargin = Math.round(dp(8));
        prefCard.addView(btnForget, fgLp);

        tabPc.addView(prefCard);
        refreshPairingSummary();
    }

    private void refreshPairingSummary() {
        if (btnQuickPair != null) {
            boolean paired = store.isPaired();
            btnQuickPair.setText(paired ? "Paired" : "Pair PC");
            btnQuickPair.setBackground(pillDrawable(
                    paired ? Color.argb(28, 255, 255, 255) : Color.rgb(239, 237, 232),
                    paired ? Color.argb(36, 255, 255, 255) : Color.TRANSPARENT,
                    16));
            btnQuickPair.setTextColor(paired ? Color.rgb(239, 237, 232) : Color.rgb(12, 12, 14));
        }
        if (store.isPaired()) {
            String fp = store.getCertFingerprint();
            String shortFp = fp.length() > 16 ? (fp.substring(0, 16) + "...") : fp;
            tvPairedSummary.setText("Paired: " + store.getPcName() + " (" + store.getPcIp() + ":" + store.getTlsPort() + ")\nCert SHA-256: " + shortFp);
        } else {
            tvPairedSummary.setText("No paired Windows PC.");
        }
    }

    private void switchTab(int idx) {
        activeTab = idx;
        if (bottomTabBar != null) bottomTabBar.setSelectedTab(idx, true);
        View[] tabs = new View[]{tabNow, tabPad, tabApps, tabModes, tabPc};
        for (int i = 0; i < tabs.length; i++) {
            boolean show = (i == idx);
            tabs[i].setVisibility(show ? View.VISIBLE : View.GONE);
            if (show) {
                tabs[i].setAlpha(0f);
                tabs[i].animate().alpha(1f).setDuration(140).start();
            }
        }
        applyLookTheme();
    }

    private static int parseHexColor(String hex, int fallback) {
        if (hex == null || hex.isEmpty()) return fallback;
        try { return Color.parseColor(hex); } catch (Exception ignored) { return fallback; }
    }

    private void applyLookTheme() {
        String look = store.getLookMode();
        // Sync theme automatically from Windows Notch when Look is "Frosted" or "Auto"
        boolean syncFromPc = "Frosted".equals(look) || "Auto".equals(look);
        if (syncFromPc && lastState != null && lastState.themeName != null && !lastState.themeName.isEmpty()) {
            lightSurface = lastState.themeIsLight;
            glassSurface = "Frosted".equals(look) || lastState.themeIsGlass;
            activeAccentColor = parseHexColor(lastState.themeAccentHex, Color.rgb(43, 228, 234));
        } else if ("Auto".equals(look)) {
            int night = getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK;
            lightSurface = (night != Configuration.UI_MODE_NIGHT_YES);
            glassSurface = true;
            activeAccentColor = Color.rgb(43, 228, 234);
        } else {
            lightSurface = "Light".equals(look);
            glassSurface = "Frosted".equals(look);
            activeAccentColor = Color.rgb(43, 228, 234);
        }
        // Reflect playing music color across the entire Android app when enabled
        if (store.isSyncMusicColorEnabled() && lastState != null && lastState.hasMedia
                && lastState.accentHex != null && !lastState.accentHex.isEmpty()) {
            activeAccentColor = parseHexColor(lastState.accentHex, activeAccentColor);
        }

        // Rich music-synced ambient backdrop across ALL tabs when Sync Music Color is on
        boolean hasMusicTint = store.isSyncMusicColorEnabled() && lastState != null && lastState.hasMedia
                && lastState.accentHex != null && !lastState.accentHex.isEmpty();
        int baseAccent = hasMusicTint
                ? parseHexColor(lastState.accentHex, activeAccentColor)
                : activeAccentColor;
        int r = Color.red(baseAccent), g = Color.green(baseAccent), b = Color.blue(baseAccent);

        if (lightSurface) {
            GradientDrawable lightBg = new GradientDrawable(
                    GradientDrawable.Orientation.TOP_BOTTOM,
                    new int[]{
                            Color.rgb(Math.min(255, 214 + r / 6), Math.min(255, 214 + g / 6), Math.min(255, 218 + b / 6)),
                            Color.rgb(242, 243, 247),
                            Color.rgb(232, 235, 242)
                    });
            ambientBackdrop.setBackground(lightBg);
        } else if (hasMusicTint) {
            // Vivid, unmistakable music-colored atmospheric gradient synced to playing song
            GradientDrawable musicBg = new GradientDrawable(
                    GradientDrawable.Orientation.TOP_BOTTOM,
                    new int[]{
                            Color.rgb(Math.min(255, 14 + (int) (r * 0.44f)), Math.min(255, 14 + (int) (g * 0.44f)), Math.min(255, 18 + (int) (b * 0.44f))),
                            Color.rgb(Math.min(255, 12 + (int) (r * 0.20f)), Math.min(255, 12 + (int) (g * 0.20f)), Math.min(255, 16 + (int) (b * 0.20f))),
                            Color.rgb(10, 11, 15)
                    });
            ambientBackdrop.setBackground(musicBg);
        } else if (glassSurface) {
            GradientDrawable glassBg = new GradientDrawable(
                    GradientDrawable.Orientation.TOP_BOTTOM,
                    new int[]{
                            Color.rgb(14 + r / 7, 16 + g / 7, 22 + b / 7),
                            Color.rgb(11 + r / 12, 13 + g / 12, 18 + b / 12),
                            Color.rgb(9, 10, 14)
                    });
            ambientBackdrop.setBackground(glassBg);
        } else {
            ambientBackdrop.setBackgroundColor(Color.rgb(10, 11, 14));
        }

        // Clean GPU blur without white glow wash
        if (glassSurface) {
            float blurPx = (lastState != null && lastState.themeBlurRadius > 4) ? lastState.themeBlurRadius * 1.2f : 22f;
            int tint = lightSurface ? Color.argb(24, 255, 255, 255) : Color.argb(28, 6, 8, 12);
            LiquidGlassHelper.applyBackdropLiquidEffect(ambientBackdrop, blurPx, dp(24), tint);
        } else {
            LiquidGlassHelper.clearEffect(ambientBackdrop);
        }

        topPill.setBackground(pillDrawable(
                lightSurface ? Color.rgb(18, 20, 24) : Color.rgb(10, 10, 12),
                Color.argb(56, 255, 255, 255), 22));
        if (bottomTabBar != null) bottomTabBar.setStyle(glassSurface, lightSurface, activeAccentColor);
        for (LiquidGlassHelper.GlassContainer card : glassCards) {
            card.setGlassStyle(glassSurface, lightSurface, activeAccentColor);
        }
        if (nowArtCard != null) {
            int artFill = lightSurface
                    ? Color.argb(210, 220 + r / 8, 220 + g / 8, 224 + b / 8)
                    : Color.argb(190, 16 + r / 4, 18 + g / 4, 24 + b / 4);
            nowArtCard.setBackground(pillDrawable(artFill, Color.argb(50, 255, 255, 255), 24));
        }

        int textMain = lightSurface ? Color.rgb(14, 15, 18) : Color.rgb(242, 241, 238);
        int textMuted = lightSurface ? Color.rgb(92, 96, 106) : Color.argb(185, 235, 236, 240);
        tvTopStatus.setTextColor(Color.rgb(242, 241, 238));
        tvMediaTitle.setTextColor(textMain);
        tvMediaArtist.setTextColor(textMuted);
        tvMediaTime.setTextColor(textMuted);
        tvVolLabel.setTextColor(textMuted);
        tvPairedSummary.setTextColor(textMuted);

        int activePill = lightSurface ? Color.rgb(14, 16, 20) : Color.argb(62, 255, 255, 255);
        int activeInk = Color.rgb(242, 241, 238);
        String[] looks = {"Frosted", "Auto", "Dark", "Light"};
        for (int i = 0; i < 4; i++) {
            boolean sel = looks[i].equals(look);
            lookButtons[i].setBackground(pillDrawable(
                    sel ? activePill : Color.argb(22, 255, 255, 255), Color.TRANSPARENT, 14));
            lookButtons[i].setTextColor(sel ? activeInk : textMuted);
        }

        String activeGrad = store.getGradientPreset();
        String[] grads = {"None", "Midnight", "Crimson", "Aurora", "Emerald"};
        for (int i = 0; i < 5; i++) {
            if (gradientButtons[i] == null) continue;
            boolean sel = grads[i].equals(activeGrad);
            gradientButtons[i].setBackground(pillDrawable(
                    sel ? activeAccentColor : Color.argb(22, 255, 255, 255), Color.TRANSPARENT, 12));
            gradientButtons[i].setTextColor(sel ? Color.rgb(10, 12, 16) : textMuted);
        }
        if (audioWaveBg != null) {
            if ("Midnight".equals(activeGrad)) {
                audioWaveBg.setCustomGradient(true, Color.rgb(14, 24, 58), Color.rgb(22, 16, 48), Color.rgb(8, 10, 18));
            } else if ("Crimson".equals(activeGrad)) {
                audioWaveBg.setCustomGradient(true, Color.rgb(62, 14, 26), Color.rgb(36, 12, 24), Color.rgb(12, 8, 12));
            } else if ("Aurora".equals(activeGrad)) {
                audioWaveBg.setCustomGradient(true, Color.rgb(14, 48, 56), Color.rgb(36, 18, 62), Color.rgb(10, 12, 20));
            } else if ("Emerald".equals(activeGrad)) {
                audioWaveBg.setCustomGradient(true, Color.rgb(12, 52, 38), Color.rgb(14, 34, 42), Color.rgb(8, 12, 14));
            } else {
                audioWaveBg.setCustomGradient(false, 0, 0, 0);
            }
        }

        cardCpu.setLightSurface(lightSurface);
        cardGpu.setLightSurface(lightSurface);
        cardRam.setLightSurface(lightSurface);
        if (nowCardCpu != null) nowCardCpu.setLightSurface(lightSurface);
        if (nowCardGpu != null) nowCardGpu.setLightSurface(lightSurface);
        if (nowCardRam != null) nowCardRam.setLightSurface(lightSurface);
        rebuildNowWidgets(lastState);
        btnSleep.setLightSurface(lightSurface);
        btnRestart.setLightSurface(lightSurface);
        btnShutdown.setLightSurface(lightSurface);
        if (sbSeek != null) sbSeek.setStyle(activeAccentColor, lightSurface);
        if (sbVolume != null) sbVolume.setStyle(activeAccentColor, lightSurface);
        if (swTrailToggle != null) swTrailToggle.setLightSurface(lightSurface);
        if (swHapticsToggle != null) swHapticsToggle.setLightSurface(lightSurface);
        if (swMediaNotif != null) swMediaNotif.setLightSurface(lightSurface);
        if (swAudioWave != null) swAudioWave.setLightSurface(lightSurface);
        if (swSyncMusicColor != null) swSyncMusicColor.setLightSurface(lightSurface);
        padCanvas.setAccentColor(activeAccentColor);
        refreshPadPrefs();
        updateAudioWaveVisibility();
    }

    private void updateAudioWaveVisibility() {
        if (audioWaveBg == null) return;
        boolean showWave = activeTab == 0 && !deckFullscreen && store.isAudioWaveEnabled();
        int col = (lastState != null && lastState.hasMedia)
                ? parseHexColor(lastState.accentHex, activeAccentColor)
                : activeAccentColor;
        int lvl = lastState != null ? lastState.audioLevel : 0;
        boolean playing = lastState != null && (lastState.isPlaying || lvl > 8);
        audioWaveBg.setWaveState(col, lvl, playing, showWave);
    }

    private void rebuildShelfAndClipboard(PinnedDeckClient.PcState st) {
        if (shelfListBox == null || clipListBox == null) return;
        shelfListBox.removeAllViews();
        if (st.shelf.isEmpty()) {
            TextView empty = new TextView(this);
            empty.setText("No files on PC Shelf yet");
            empty.setTextSize(12f);
            empty.setTextColor(Color.argb(145, 220, 225, 235));
            shelfListBox.addView(empty);
        } else {
            for (PinnedDeckClient.ShelfItem sf : st.shelf) {
                shelfListBox.addView(makeShelfItemView(sf));
            }
        }

        clipListBox.removeAllViews();
        if (st.clipboard.isEmpty()) {
            TextView empty = new TextView(this);
            empty.setText("No PC Clipboard history yet");
            empty.setTextSize(12f);
            empty.setTextColor(Color.argb(145, 220, 225, 235));
            clipListBox.addView(empty);
        } else {
            for (final PinnedDeckClient.ClipItem ci : st.clipboard) {
                TextView row = new TextView(this);
                row.setText((ci.pinned ? "📌  " : "📋  ") + ci.text.replace('\n', ' '));
                row.setTextSize(12.5f);
                row.setSingleLine(true);
                row.setTextColor(Color.WHITE);
                row.setBackground(pillDrawable(Color.argb(32, 255, 255, 255), Color.argb(28, 255, 255, 255), 12));
                row.setPadding(Math.round(dp(12)), Math.round(dp(9)), Math.round(dp(12)), Math.round(dp(9)));
                row.setOnClickListener(v -> {
                    try {
                        ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
                        if (cm != null) cm.setPrimaryClip(ClipData.newPlainText("NotchPixel", ci.text));
                        Toast.makeText(this, "Copied to phone clipboard", Toast.LENGTH_SHORT).show();
                    } catch (Exception ignored) {}
                });
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
                lp.bottomMargin = Math.round(dp(6));
                clipListBox.addView(row, lp);
            }
        }
        if (todoListBox != null) {
            todoListBox.removeAllViews();
            if (st.todos.isEmpty()) {
                TextView empty = new TextView(this);
                empty.setText("No PC To-Do items yet — tap + Add");
                empty.setTextSize(12f);
                empty.setTextColor(Color.argb(145, 220, 225, 235));
                todoListBox.addView(empty);
            } else {
                for (final PinnedDeckClient.TodoItem ti : st.todos) {
                    TextView row = new TextView(this);
                    row.setText((ti.done ? "☑  " : "☐  ") + ti.text);
                    row.setTextSize(12.5f);
                    row.setSingleLine(true);
                    row.setTextColor(ti.done ? Color.argb(145, 200, 208, 220) : Color.WHITE);
                    row.setBackground(pillDrawable(
                            ti.done ? Color.argb(20, 48, 209, 88) : Color.argb(32, 255, 255, 255),
                            Color.argb(28, 255, 255, 255), 12));
                    row.setPadding(Math.round(dp(12)), Math.round(dp(9)), Math.round(dp(12)), Math.round(dp(9)));
                    row.setOnClickListener(v -> sendAsync(() -> client.toggleTodo(ti.index)));
                    LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
                    lp.bottomMargin = Math.round(dp(6));
                    todoListBox.addView(row, lp);
                }
            }
        }
    }

    private void setStatusDotStyle(int dotColor, String badgeText, int badgeBg, int badgeFg) {
        if (topStatusDot != null) {
            GradientDrawable oval = new GradientDrawable();
            oval.setShape(GradientDrawable.OVAL);
            oval.setColor(dotColor);
            topStatusDot.setBackground(oval);
        }
        if (tvTopBadge != null) {
            tvTopBadge.setText(badgeText);
            tvTopBadge.setTextColor(badgeFg);
            tvTopBadge.setBackground(pillDrawable(badgeBg, Color.TRANSPARENT, 10));
        }
    }

    private void applyStateToUi(PinnedDeckClient.PcState st, boolean connected) {
        lastState = st;
        tvBannerNotice.setVisibility(View.GONE);

        // Live theme sync from Windows Notch (only re-applies when theme or album color changes)
        String themeSig = st.themeName + "|" + st.themeIsLight + "|" + st.themeIsGlass + "|"
                + st.themeAccentHex + "|" + (st.hasMedia ? st.accentHex : "");
        if (!themeSig.equals(lastThemeSignature)) {
            lastThemeSignature = themeSig;
            applyLookTheme();
        }

        if (st.recording) {
            setStatusDotStyle(Color.rgb(255, 69, 58), "REC " + fmtSec(st.recordingSec),
                    Color.argb(65, 255, 69, 58), Color.rgb(255, 130, 124));
            tvTopStatus.setText(st.pcName);
        } else {
            setStatusDotStyle(Color.rgb(48, 209, 88), "LIVE",
                    Color.argb(52, 48, 209, 88), Color.rgb(90, 232, 130));
            tvTopStatus.setText(st.pcName);
        }

        if (st.hasMedia) {
            tvMediaApp.setText((st.mediaApp.isEmpty() ? "PC MEDIA" : st.mediaApp.toUpperCase(Locale.US)));
            tvMediaTitle.setText(st.mediaTitle.isEmpty() ? "Playing" : st.mediaTitle);
            tvMediaArtist.setText(st.mediaArtist.isEmpty() ? "Windows Media Session" : st.mediaArtist);
            if (!isSeekingMedia) {
                int pct = st.durSec > 0 ? Math.min(100, Math.round(st.posSec * 100f / st.durSec)) : 0;
                sbSeek.setProgress(pct);
                tvMediaTime.setText(fmtSec(st.posSec) + " / " + fmtSec(st.durSec));
            }
            btnPlayPause.setText(st.isPlaying ? "❚❚" : "▶");
            if (ivNowArt != null && !st.albumArtB64.equals(lastArtB64)) {
                lastArtB64 = st.albumArtB64;
                Bitmap artBmp = decodeBase64Bitmap(st.albumArtB64);
                if (artBmp != null) {
                    ivNowArt.setImageBitmap(artBmp);
                    ivNowArt.setVisibility(View.VISIBLE);
                    tvNowArtGlyph.setVisibility(View.GONE);
                } else {
                    ivNowArt.setVisibility(View.GONE);
                    tvNowArtGlyph.setVisibility(View.VISIBLE);
                }
            }
        } else {
            tvMediaApp.setText("NOW PLAYING ON PC");
            tvMediaTitle.setText("Nothing playing");
            tvMediaArtist.setText("Play music or video on your PC");
            if (!isSeekingMedia) {
                sbSeek.setProgress(0);
                tvMediaTime.setText("0:00 / 0:00");
            }
            btnPlayPause.setText("▶");
            if (ivNowArt != null && !lastArtB64.isEmpty()) {
                lastArtB64 = "";
                ivNowArt.setVisibility(View.GONE);
                tvNowArtGlyph.setVisibility(View.VISIBLE);
            }
        }
        updateAudioWaveVisibility();

        if (!isDraggingVolume) {
            tvVolLabel.setText(st.muted ? "0" : String.valueOf(st.volume));
            sbVolume.setProgress(st.volume);
        }
        btnMuteVol.setText(st.muted ? "🔇" : "🔊");
        btnMuteMic.setText(st.micMuted ? "Mic: Muted" : "Mic: Active");

        cardCpu.updateMetric(st.cpu, st.cpuTempAvailable, st.cpuTempC);
        cardGpu.updateMetric(st.gpu, st.gpuTempAvailable, st.gpuTempC);
        cardRam.updateMetric(st.ram, false, 0);
        if (nowCardCpu != null) nowCardCpu.updateMetric(st.cpu, st.cpuTempAvailable, st.cpuTempC);
        if (nowCardGpu != null) nowCardGpu.updateMetric(st.gpu, st.gpuTempAvailable, st.gpuTempC);
        if (nowCardRam != null) nowCardRam.updateMetric(st.ram, false, 0);

        if (tvRecTimer != null && activeTab == 3 && activeModeSub == 0) {
            tvRecTimer.setText(st.recording ? ("Recording  " + fmtSec(st.recordingSec)) : "Game Deck Ready");
            tvRecTimer.setTextColor(st.recording ? Color.rgb(242, 72, 72) : activeAccentColor);
        }
        if (btnGameRecord != null && activeTab == 3 && activeModeSub == 0) {
            btnGameRecord.setBackground(pillDrawable(
                    st.recording ? Color.rgb(210, 48, 48) : Color.rgb(34, 40, 50),
                    Color.argb(50, 255, 255, 255), 16));
        }
        if (tvPresentTimer != null && activeTab == 3 && activeModeSub == 1 && presentingActive) {
            int el = (int) ((System.currentTimeMillis() - presentStartMs) / 1000L);
            tvPresentTimer.setText("Presentation Timer: " + fmtSec(el));
        }

        // Only rebuild Stream Deck Apps grid when slots, icons, or mute state actually changed
        StringBuilder sig = new StringBuilder();
        sig.append(st.micMuted).append('|').append(st.muted).append('|').append(lightSurface).append('|').append(deckFullscreen);
        for (PinnedDeckClient.AppSlot s : st.apps) {
            sig.append('|').append(s.label).append(':').append(s.configured).append(':').append(s.iconB64.length());
        }
        String appsSig = sig.toString();
        if (!appsSig.equals(lastAppsSignature)) {
            lastAppsSignature = appsSig;
            rebuildAppsGrid();
        }

        // Only rebuild Shelf, Clipboard & To-Do when changed
        StringBuilder scSig = new StringBuilder();
        for (PinnedDeckClient.ShelfItem sf : st.shelf) scSig.append(sf.name).append(':').append(sf.sizeBytes).append('|');
        scSig.append('#');
        for (PinnedDeckClient.ClipItem ci : st.clipboard) scSig.append(ci.text).append(':').append(ci.pinned).append('|');
        scSig.append('#');
        for (PinnedDeckClient.TodoItem ti : st.todos) scSig.append(ti.text).append(':').append(ti.done).append('|');
        String scStr = scSig.toString();
        if (!scStr.equals(lastShelfClipSig)) {
            lastShelfClipSig = scStr;
            rebuildShelfAndClipboard(st);
            rebuildNowWidgets(st);
        }
        refreshPairingSummary();

        if (store.isMediaNotifEnabled() && st.hasMedia) {
            DeckMediaNotificationService.updatePlayback(this, st.mediaTitle, st.mediaArtist, st.isPlaying);
        }
    }

    private void applyDisconnectedState(String reason) {
        setStatusDotStyle(Color.rgb(240, 165, 65),
                store.isPaired() ? "OFFLINE" : "PAIR",
                Color.argb(56, 240, 165, 65), Color.rgb(255, 200, 110));
        tvTopStatus.setText(store.isPaired() ? store.getPcName() : "Pair PC");
        tvBannerNotice.setText(reason == null ? "Waiting for Private Wi-Fi connection..." : reason);
        tvBannerNotice.setTextColor(Color.rgb(240, 185, 85));
        tvBannerNotice.setVisibility(View.VISIBLE);
        refreshPairingSummary();
    }

    private interface RemoteOp {
        void run() throws Exception;
    }

    private void sendAsync(RemoteOp op) {
        if (!store.isPaired()) {
            runOnUiThread(() -> Toast.makeText(this, "Pair with your PC first", Toast.LENGTH_SHORT).show());
            return;
        }
        cmdExecutor.execute(() -> {
            try { op.run(); }
            catch (final Exception ex) {
                boolean is401 = (ex instanceof SecurityException)
                        && ex.getMessage() != null
                        && ex.getMessage().contains("HTTP 401");
                if (is401) {
                    deviceRevoked = true;
                    runOnUiThread(() -> {
                        DeckMediaNotificationService.stopService(MainActivity.this);
                        applyDisconnectedState("Device removed on PC — tap Pair PC to re-pair.");
                    });
                } else {
                    runOnUiThread(() -> applyDisconnectedState(ex.getMessage()));
                }
            }
        });
    }

    private static String fmtSec(int sec) {
        int s = Math.max(0, sec);
        return String.format(Locale.US, "%d:%02d", s / 60, s % 60);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        if (requestCode == REQ_PAIR && resultCode == RESULT_OK) {
            store.reload();
            deviceRevoked = false;
            refreshPairingSummary();
            uiHandler.removeCallbacks(pollRunnable);
            uiHandler.post(pollRunnable);
            Toast.makeText(this, "Connected to " + store.getPcName(), Toast.LENGTH_SHORT).show();
        }
        super.onActivityResult(requestCode, resultCode, data);
    }

    @Override
    protected void onDestroy() {
        uiHandler.removeCallbacks(pollRunnable);
        pollExecutor.shutdownNow();
        cmdExecutor.shutdownNow();
        padUdpExecutor.shutdownNow();
        super.onDestroy();
    }
}
