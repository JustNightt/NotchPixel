package com.openpixel.deck.ui;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import com.openpixel.deck.network.WakeOnLanSender;
import com.openpixel.deck.security.PinnedDeckClient;
import com.openpixel.deck.security.SecurePairingStore;
import com.openpixel.deck.service.DeckMediaNotificationService;

import java.net.URLEncoder;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * OpenPixel Deck floating pill control surface for Android.
 * Tabs: Now, Pad, Apps, Modes (Game, Present, Movie, Work), and PC.
 */
public final class MainActivity extends Activity {
    private static final int REQ_PAIR = 2010;

    private SecurePairingStore store;
    private PinnedDeckClient client;
    private ExecutorService netExecutor;
    private Handler uiHandler;

    private int activeTab = 0;      // 0=Now, 1=Pad, 2=Apps, 3=Modes, 4=PC
    private int activeModeSub = 0;  // 0=Game, 1=Present, 2=Movie, 3=Work
    private long presentStartMs = 0L;
    private boolean presentingActive = false;

    private LinearLayout rootLayout;
    private LinearLayout topPill;
    private TextView tvTopStatus, tvTopAccentDot, tvBannerNotice;
    private FrameLayout contentHost;
    private LinearLayout bottomTabBar;
    private final Button[] tabButtons = new Button[5];

    // Now tab views
    private LinearLayout tabNow;
    private TextView tvMediaTitle, tvMediaArtist, tvMediaApp, tvMediaTime, tvVolLabel;
    private SeekBar sbSeek, sbVolume;
    private Button btnPlayPause, btnMuteVol, btnMuteMic;

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

    // PC tab views
    private LinearLayout tabPc;
    private LiquidStatCardView cardCpu, cardGpu, cardRam;
    private HoldPowerButton btnSleep, btnRestart, btnShutdown;
    private TextView tvPairedSummary, tvPcTabNotice;
    private final Button[] lookButtons = new Button[4];
    private Switch swMediaNotif;

    private PinnedDeckClient.PcState lastState = new PinnedDeckClient.PcState();
    private boolean lightSurface = false;

    private final Runnable pollRunnable = new Runnable() {
        @Override
        public void run() {
            if (store.isPaired()) {
                netExecutor.execute(() -> {
                    try {
                        PinnedDeckClient.PcState st = client.fetchState();
                        runOnUiThread(() -> applyStateToUi(st, true));
                    } catch (Exception ex) {
                        runOnUiThread(() -> applyDisconnectedState(ex.getMessage()));
                    }
                });
            } else {
                applyDisconnectedState("Not paired yet — tap Pair PC");
            }
            uiHandler.postDelayed(this, 1200L);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        store = new SecurePairingStore(this);
        client = new PinnedDeckClient(this, store);
        netExecutor = Executors.newSingleThreadExecutor();
        uiHandler = new Handler(Looper.getMainLooper());

        buildUi();
        applyLookTheme();
        switchTab(0);
    }

    @Override
    protected void onResume() {
        super.onResume();
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
        rootLayout = new LinearLayout(this);
        rootLayout.setOrientation(LinearLayout.VERTICAL);
        rootLayout.setPadding(Math.round(dp(14)), Math.round(dp(14)), Math.round(dp(14)), Math.round(dp(12)));

        // Top floating OpenPixel status pill
        topPill = new LinearLayout(this);
        topPill.setOrientation(LinearLayout.HORIZONTAL);
        topPill.setGravity(Gravity.CENTER_VERTICAL);
        topPill.setPadding(Math.round(dp(16)), Math.round(dp(10)), Math.round(dp(14)), Math.round(dp(10)));

        tvTopAccentDot = new TextView(this);
        tvTopAccentDot.setText("■ ");
        tvTopAccentDot.setTextColor(Color.rgb(43, 228, 234));
        tvTopAccentDot.setTextSize(13f);
        topPill.addView(tvTopAccentDot);

        tvTopStatus = new TextView(this);
        tvTopStatus.setText(store.isPaired() ? store.getPcName() : "Desk PC (Unpaired)");
        tvTopStatus.setTextSize(15f);
        tvTopStatus.getPaint().setFakeBoldText(true);
        topPill.addView(tvTopStatus, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        Button btnQuickPair = new Button(this);
        btnQuickPair.setText(store.isPaired() ? "Pair / Sync" : "Pair PC");
        btnQuickPair.setAllCaps(false);
        btnQuickPair.setTextSize(12f);
        btnQuickPair.setTextColor(Color.rgb(8, 12, 16));
        btnQuickPair.setBackground(pillDrawable(Color.rgb(43, 228, 234), Color.TRANSPARENT, 14));
        btnQuickPair.setPadding(Math.round(dp(12)), Math.round(dp(4)), Math.round(dp(12)), Math.round(dp(4)));
        btnQuickPair.setOnClickListener(v -> startActivityForResult(new Intent(this, QrPairingActivity.class), REQ_PAIR));
        topPill.addView(btnQuickPair);

        LinearLayout.LayoutParams topLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        topLp.bottomMargin = Math.round(dp(10));
        rootLayout.addView(topPill, topLp);

        tvBannerNotice = new TextView(this);
        tvBannerNotice.setTextSize(12f);
        tvBannerNotice.setPadding(Math.round(dp(6)), 0, Math.round(dp(6)), Math.round(dp(8)));
        tvBannerNotice.setVisibility(View.GONE);
        rootLayout.addView(tvBannerNotice);

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
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

        // Floating 5-tab bottom pill
        bottomTabBar = new LinearLayout(this);
        bottomTabBar.setOrientation(LinearLayout.HORIZONTAL);
        bottomTabBar.setPadding(Math.round(dp(6)), Math.round(dp(6)), Math.round(dp(6)), Math.round(dp(6)));
        String[] tabNames = new String[]{"Now", "Pad", "Apps", "Modes", "PC"};
        for (int i = 0; i < 5; i++) {
            final int idx = i;
            Button b = new Button(this);
            b.setText(tabNames[i]);
            b.setAllCaps(false);
            b.setTextSize(12.5f);
            b.getPaint().setFakeBoldText(true);
            b.setPadding(Math.round(dp(4)), Math.round(dp(8)), Math.round(dp(4)), Math.round(dp(8)));
            b.setOnClickListener(v -> switchTab(idx));
            tabButtons[i] = b;
            bottomTabBar.addView(b, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        }
        LinearLayout.LayoutParams botLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        botLp.topMargin = Math.round(dp(10));
        rootLayout.addView(bottomTabBar, botLp);

        setContentView(rootLayout);
    }

    private LinearLayout makeCardContainer() {
        LinearLayout ll = new LinearLayout(this);
        ll.setOrientation(LinearLayout.VERTICAL);
        ll.setPadding(Math.round(dp(16)), Math.round(dp(16)), Math.round(dp(16)), Math.round(dp(16)));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Math.round(dp(12));
        ll.setLayoutParams(lp);
        ll.setBackground(pillDrawable(
                lightSurface ? Color.argb(235, 242, 245, 250) : Color.argb(225, 21, 25, 33),
                lightSurface ? Color.argb(50, 25, 35, 50) : Color.argb(55, 255, 255, 255),
                22));
        return ll;
    }

    private Button makeActionBtn(String text, int bg, int fg) {
        Button b = new Button(this);
        b.setText(text);
        b.setAllCaps(false);
        b.setTextColor(fg);
        b.setTextSize(13f);
        b.getPaint().setFakeBoldText(true);
        b.setBackground(pillDrawable(bg, Color.argb(50, 255, 255, 255), 16));
        b.setPadding(Math.round(dp(12)), Math.round(dp(10)), Math.round(dp(12)), Math.round(dp(10)));
        return b;
    }

    private void buildTabNow() {
        tabNow = new LinearLayout(this);
        tabNow.setOrientation(LinearLayout.VERTICAL);

        LinearLayout mediaCard = makeCardContainer();
        tvMediaApp = new TextView(this);
        tvMediaApp.setText("NOW PLAYING ON PC");
        tvMediaApp.setTextSize(11f);
        tvMediaApp.getPaint().setFakeBoldText(true);
        tvMediaApp.setTextColor(Color.rgb(43, 228, 234));
        mediaCard.addView(tvMediaApp);

        tvMediaTitle = new TextView(this);
        tvMediaTitle.setText("Nothing playing on PC");
        tvMediaTitle.setTextSize(20f);
        tvMediaTitle.getPaint().setFakeBoldText(true);
        tvMediaTitle.setPadding(0, Math.round(dp(8)), 0, Math.round(dp(2)));
        mediaCard.addView(tvMediaTitle);

        tvMediaArtist = new TextView(this);
        tvMediaArtist.setText("Start Spotify, Tidal, YouTube, or Edge/Chrome on your PC");
        tvMediaArtist.setTextSize(13f);
        mediaCard.addView(tvMediaArtist);

        sbSeek = new SeekBar(this);
        sbSeek.setMax(100);
        sbSeek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar sb, int p, boolean fromUser) {
                if (fromUser && lastState.durSec > 0) {
                    int sec = (int) (lastState.durSec * (p / 100.0));
                    sendAsync(() -> client.sendMediaAction("a=seek&v=" + sec));
                }
            }
            @Override public void onStartTrackingTouch(SeekBar sb) {}
            @Override public void onStopTrackingTouch(SeekBar sb) {}
        });
        LinearLayout.LayoutParams sLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        sLp.topMargin = Math.round(dp(12));
        mediaCard.addView(sbSeek, sLp);

        tvMediaTime = new TextView(this);
        tvMediaTime.setText("0:00 / 0:00");
        tvMediaTime.setTextSize(11.5f);
        mediaCard.addView(tvMediaTime);

        LinearLayout transport = new LinearLayout(this);
        transport.setOrientation(LinearLayout.HORIZONTAL);
        transport.setPadding(0, Math.round(dp(12)), 0, 0);

        Button btnPrev = makeActionBtn("Prev", Color.rgb(32, 37, 47), Color.WHITE);
        btnPrev.setOnClickListener(v -> sendAsync(() -> client.sendMediaAction("a=prev")));
        btnPlayPause = makeActionBtn("Play / Pause", Color.rgb(43, 228, 234), Color.rgb(8, 12, 16));
        btnPlayPause.setOnClickListener(v -> sendAsync(() -> client.sendMediaAction("a=play")));
        Button btnNext = makeActionBtn("Next", Color.rgb(32, 37, 47), Color.WHITE);
        btnNext.setOnClickListener(v -> sendAsync(() -> client.sendMediaAction("a=next")));

        LinearLayout.LayoutParams tLp1 = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        tLp1.rightMargin = Math.round(dp(8));
        LinearLayout.LayoutParams tLp2 = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1.4f);
        tLp2.rightMargin = Math.round(dp(8));
        transport.addView(btnPrev, tLp1);
        transport.addView(btnPlayPause, tLp2);
        transport.addView(btnNext, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        mediaCard.addView(transport);
        tabNow.addView(mediaCard);

        // Volume & Mic card
        LinearLayout audioCard = makeCardContainer();
        tvVolLabel = new TextView(this);
        tvVolLabel.setText("PC Volume: --%");
        tvVolLabel.setTextSize(14f);
        tvVolLabel.getPaint().setFakeBoldText(true);
        audioCard.addView(tvVolLabel);

        sbVolume = new SeekBar(this);
        sbVolume.setMax(100);
        sbVolume.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar sb, int p, boolean fromUser) {
                if (fromUser) {
                    tvVolLabel.setText("PC Volume: " + p + "%");
                    sendAsync(() -> client.sendMediaAction("a=vol&v=" + p));
                }
            }
            @Override public void onStartTrackingTouch(SeekBar sb) {}
            @Override public void onStopTrackingTouch(SeekBar sb) {}
        });
        audioCard.addView(sbVolume);

        LinearLayout audioRow = new LinearLayout(this);
        audioRow.setOrientation(LinearLayout.HORIZONTAL);
        audioRow.setPadding(0, Math.round(dp(10)), 0, 0);
        btnMuteVol = makeActionBtn("Mute Audio", Color.rgb(32, 37, 47), Color.WHITE);
        btnMuteVol.setOnClickListener(v -> sendAsync(() -> client.sendMediaAction("a=mute")));
        btnMuteMic = makeActionBtn("Mic: Active", Color.rgb(32, 37, 47), Color.WHITE);
        btnMuteMic.setOnClickListener(v -> sendAsync(() -> client.sendModeAction("mic")));
        LinearLayout.LayoutParams mLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        mLp.rightMargin = Math.round(dp(8));
        audioRow.addView(btnMuteVol, mLp);
        audioRow.addView(btnMuteMic, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        audioCard.addView(audioRow);
        tabNow.addView(audioCard);
    }

    private void buildTabPad() {
        tabPad = new LinearLayout(this);
        tabPad.setOrientation(LinearLayout.VERTICAL);

        padCanvas = new PadCanvasView(this, new PadCanvasView.PadActionSink() {
            @Override public void onMove(int dx, int dy) { sendAsync(() -> client.sendPadAction("a=move&dx=" + dx + "&dy=" + dy)); }
            @Override public void onScroll(int dy) { sendAsync(() -> client.sendPadAction("a=scroll&dy=" + dy)); }
            @Override public void onLeftClick() { sendAsync(() -> client.sendPadAction("a=lclick")); }
            @Override public void onRightClick() { sendAsync(() -> client.sendPadAction("a=rclick")); }
            @Override public void onDragStart() { sendAsync(() -> client.sendPadAction("a=drag_start")); }
            @Override public void onDragEnd() { sendAsync(() -> client.sendPadAction("a=drag_end")); }
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
        typeRow.addView(etPadType, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        Button btnSendType = makeActionBtn("Send", Color.rgb(43, 228, 234), Color.rgb(8, 12, 16));
        btnSendType.setOnClickListener(v -> {
            final String txt = etPadType.getText().toString();
            if (!txt.isEmpty()) {
                etPadType.setText("");
                sendAsync(() -> client.sendPadAction("a=type&text=" + URLEncoder.encode(txt, "UTF-8")));
            }
        });
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

        Switch swTrail = new Switch(this);
        swTrail.setText("Cyan dot-matrix pointer trail");
        swTrail.setChecked(store.isTrailEnabled());
        swTrail.setOnCheckedChangeListener((btn, checked) -> { store.setTrailEnabled(checked); refreshPadPrefs(); });
        kbCard.addView(swTrail);

        Switch swHaptics = new Switch(this);
        swHaptics.setText("Subtle touch haptics");
        swHaptics.setChecked(store.isHapticsEnabled());
        swHaptics.setOnCheckedChangeListener((btn, checked) -> { store.setHapticsEnabled(checked); refreshPadPrefs(); });
        kbCard.addView(swHaptics);

        tabPad.addView(kbCard);
        refreshPadPrefs();
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
        TextView hdr = new TextView(this);
        hdr.setText("PC Launcher Apps (6 slots synced from OpenPixel)");
        hdr.setTextSize(14f);
        hdr.getPaint().setFakeBoldText(true);
        hdr.setPadding(0, 0, 0, Math.round(dp(10)));
        card.addView(hdr);

        appsGrid = new GridLayout(this);
        appsGrid.setColumnCount(2);
        appsGrid.setUseDefaultMargins(true);
        card.addView(appsGrid);

        Button btnAddSlot = makeActionBtn("+ Add / Pin App on PC", Color.rgb(43, 228, 234), Color.rgb(8, 12, 16));
        btnAddSlot.setOnClickListener(v -> showAddAppDialog());
        LinearLayout.LayoutParams aLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        aLp.topMargin = Math.round(dp(10));
        card.addView(btnAddSlot, aLp);

        tabApps.addView(card);
        rebuildAppsGrid();
    }

    private void rebuildAppsGrid() {
        appsGrid.removeAllViews();
        int count = Math.max(6, lastState.apps.size());
        for (int i = 0; i < 6; i++) {
            final int slotIdx = i;
            PinnedDeckClient.AppSlot slot = i < lastState.apps.size() ? lastState.apps.get(i) : null;
            String label = (slot != null && slot.configured) ? slot.label : ("Empty Slot " + (i + 1));
            Button b = makeActionBtn(label,
                    (slot != null && slot.configured) ? Color.rgb(33, 41, 54) : Color.rgb(24, 27, 34),
                    (slot != null && slot.configured) ? Color.WHITE : Color.rgb(125, 134, 148));
            b.setOnClickListener(v -> {
                if (slot != null && slot.configured) {
                    sendAsync(() -> client.launchAppSlot(slotIdx));
                } else {
                    showAddAppDialog();
                }
            });
            GridLayout.LayoutParams glp = new GridLayout.LayoutParams();
            glp.width = 0;
            glp.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
            glp.height = Math.round(dp(64));
            appsGrid.addView(b, glp);
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
        Button btnWake = makeActionBtn("Wake PC (WOL)", Color.rgb(26, 62, 70), Color.rgb(43, 228, 234));
        btnWake.setOnClickListener(v -> netExecutor.execute(() -> {
            boolean sent = WakeOnLanSender.sendMagicPacket(store.getMacAddress());
            runOnUiThread(() -> Toast.makeText(this,
                    sent ? "Wake-on-LAN magic packet sent" : "No MAC address stored (pair first)",
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

        // Look & Notification & Pairing card
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

        swMediaNotif = new Switch(this);
        swMediaNotif.setText("Lock-screen Media Notification (stops after 10 min idle)");
        swMediaNotif.setChecked(store.isMediaNotifEnabled());
        swMediaNotif.setOnCheckedChangeListener((b, on) -> {
            store.setMediaNotifEnabled(on);
            if (!on) DeckMediaNotificationService.stopService(this);
        });
        prefCard.addView(swMediaNotif);

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
                .setMessage("Removes encrypted Keystore token and pinned PC certificate fingerprint from this phone.")
                .setPositiveButton("Forget PC", (d, w) -> {
                    store.forgetPc();
                    DeckMediaNotificationService.stopService(this);
                    applyDisconnectedState("Paired PC forgotten on this phone.");
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
        tabNow.setVisibility(idx == 0 ? View.VISIBLE : View.GONE);
        tabPad.setVisibility(idx == 1 ? View.VISIBLE : View.GONE);
        tabApps.setVisibility(idx == 2 ? View.VISIBLE : View.GONE);
        tabModes.setVisibility(idx == 3 ? View.VISIBLE : View.GONE);
        tabPc.setVisibility(idx == 4 ? View.VISIBLE : View.GONE);
        applyLookTheme();
    }

    private void applyLookTheme() {
        String look = store.getLookMode();
        if ("Auto".equals(look)) {
            int night = getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK;
            lightSurface = (night != Configuration.UI_MODE_NIGHT_YES);
        } else {
            lightSurface = "Light".equals(look);
        }

        int rootBg = "Frosted".equals(look)
                ? Color.rgb(12, 16, 22)
                : (lightSurface ? Color.rgb(228, 234, 244) : Color.rgb(8, 9, 12));
        rootLayout.setBackgroundColor(rootBg);

        int pillBg = lightSurface ? Color.argb(240, 245, 248, 253) : Color.argb(235, 20, 24, 31);
        int pillStroke = lightSurface ? Color.argb(65, 25, 35, 50) : Color.argb(65, 255, 255, 255);
        topPill.setBackground(pillDrawable(pillBg, pillStroke, 24));
        bottomTabBar.setBackground(pillDrawable(pillBg, pillStroke, 24));

        int textMain = lightSurface ? Color.rgb(18, 22, 28) : Color.rgb(245, 247, 250);
        int textMuted = lightSurface ? Color.rgb(85, 95, 110) : Color.rgb(155, 165, 180);
        tvTopStatus.setTextColor(textMain);
        tvMediaTitle.setTextColor(textMain);
        tvMediaArtist.setTextColor(textMuted);
        tvMediaTime.setTextColor(textMuted);
        tvVolLabel.setTextColor(textMain);
        tvPairedSummary.setTextColor(textMuted);

        for (int i = 0; i < 5; i++) {
            boolean sel = i == activeTab;
            tabButtons[i].setBackground(pillDrawable(
                    sel ? Color.rgb(43, 228, 234) : Color.TRANSPARENT, Color.TRANSPARENT, 18));
            tabButtons[i].setTextColor(sel ? Color.rgb(8, 12, 16) : textMuted);
        }

        String[] looks = {"Frosted", "Auto", "Dark", "Light"};
        for (int i = 0; i < 4; i++) {
            boolean sel = looks[i].equals(look);
            lookButtons[i].setBackground(pillDrawable(
                    sel ? Color.rgb(43, 228, 234) : Color.rgb(32, 37, 47), Color.TRANSPARENT, 12));
            lookButtons[i].setTextColor(sel ? Color.rgb(8, 12, 16) : Color.WHITE);
        }

        cardCpu.setLightSurface(lightSurface);
        cardGpu.setLightSurface(lightSurface);
        cardRam.setLightSurface(lightSurface);
        btnSleep.setLightSurface(lightSurface);
        btnRestart.setLightSurface(lightSurface);
        btnShutdown.setLightSurface(lightSurface);
        refreshPadPrefs();
    }

    private void applyStateToUi(PinnedDeckClient.PcState st, boolean connected) {
        lastState = st;
        tvBannerNotice.setVisibility(View.GONE);
        if (st.recording) {
            tvTopAccentDot.setTextColor(Color.rgb(242, 72, 72));
            tvTopStatus.setText("Recording  " + fmtSec(st.recordingSec));
        } else {
            tvTopAccentDot.setTextColor(Color.rgb(43, 228, 234));
            tvTopStatus.setText(st.pcName);
        }

        if (st.hasMedia) {
            tvMediaApp.setText((st.mediaApp.isEmpty() ? "PC MEDIA" : st.mediaApp.toUpperCase(Locale.US)));
            tvMediaTitle.setText(st.mediaTitle.isEmpty() ? "Playing" : st.mediaTitle);
            tvMediaArtist.setText(st.mediaArtist.isEmpty() ? "Windows Media Session" : st.mediaArtist);
            int pct = st.durSec > 0 ? Math.min(100, Math.round(st.posSec * 100f / st.durSec)) : 0;
            sbSeek.setProgress(pct);
            tvMediaTime.setText(fmtSec(st.posSec) + " / " + fmtSec(st.durSec));
            btnPlayPause.setText(st.isPlaying ? "Pause" : "Play");
        } else {
            tvMediaApp.setText("NOW PLAYING ON PC");
            tvMediaTitle.setText("Nothing playing on PC");
            tvMediaArtist.setText("No active PC media session");
            sbSeek.setProgress(0);
            tvMediaTime.setText("0:00 / 0:00");
            btnPlayPause.setText("Play / Pause");
        }

        tvVolLabel.setText(st.muted ? "PC Volume: Muted" : ("PC Volume: " + st.volume + "%"));
        sbVolume.setProgress(st.volume);
        btnMuteVol.setText(st.muted ? "Unmute" : "Mute Audio");
        btnMuteMic.setText(st.micMuted ? "Mic: Muted" : "Mic: Active");

        cardCpu.updateMetric(st.cpu, st.cpuTempAvailable, st.cpuTempC);
        cardGpu.updateMetric(st.gpu, st.gpuTempAvailable, st.gpuTempC);
        cardRam.updateMetric(st.ram, false, 0);

        if (tvRecTimer != null && activeTab == 3 && activeModeSub == 0) {
            tvRecTimer.setText(st.recording ? ("Recording  " + fmtSec(st.recordingSec)) : "Game Deck Ready");
        }
        if (tvPresentTimer != null && activeTab == 3 && activeModeSub == 1 && presentingActive) {
            int el = (int) ((System.currentTimeMillis() - presentStartMs) / 1000L);
            tvPresentTimer.setText("Presentation Timer: " + fmtSec(el));
        }

        if (activeTab == 2) rebuildAppsGrid();
        refreshPairingSummary();

        if (store.isMediaNotifEnabled() && st.hasMedia) {
            DeckMediaNotificationService.updatePlayback(this, st.mediaTitle, st.mediaArtist, st.isPlaying);
        }
    }

    private void applyDisconnectedState(String reason) {
        tvTopAccentDot.setTextColor(Color.rgb(240, 165, 65));
        tvTopStatus.setText(store.isPaired() ? (store.getPcName() + " (Offline)") : "Desk PC (Unpaired)");
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
        netExecutor.execute(() -> {
            try { op.run(); }
            catch (final Exception ex) {
                runOnUiThread(() -> applyDisconnectedState(ex.getMessage()));
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
            refreshPairingSummary();
            Toast.makeText(this, "Paired securely with certificate pinning!", Toast.LENGTH_SHORT).show();
        }
        super.onActivityResult(requestCode, resultCode, data);
    }

    @Override
    protected void onDestroy() {
        uiHandler.removeCallbacks(pollRunnable);
        netExecutor.shutdownNow();
        super.onDestroy();
    }
}
