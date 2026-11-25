#pragma once
#include "../core/Types.h"
#include "../core/ConfigManager.h"

namespace np {

// ============================================================================
// Hit-Test Action Region Registered During Frame Render
// ============================================================================
struct HitRegion {
    std::string id;
    D2D1_RECT_F rect;
    std::function<void()> onClick;
    std::function<void(float normX)> onDrag;
};

// ============================================================================
// Direct2D 1.1 Hardware/Software Layered Surface Renderer
// Includes Notch Concave Flare Geometry, Liquid Glass Blur & Refraction,
// Static Background Hash Caching (0% idle CPU), and Theme Particle Ambience
// ============================================================================
class D2DRenderer {
public:
    D2DRenderer();
    ~D2DRenderer();

    bool Initialize(bool forceSoftwareCpu = false);
    void Shutdown();

    bool BeginLayeredFrame(HWND hwnd, int widthPx, int heightPx, float dpiScale);
    void EndLayeredFrame(HWND hwnd, POINT windowScreenPos, BYTE globalAlpha = 255);

    ID2D1RenderTarget* RT() const { return m_dcRenderTarget; }
    ID2D1Factory* Factory() const { return m_d2dFactory; }
    IDWriteFactory* WriteFactory() const { return m_dwriteFactory; }
    float DpiScale() const { return m_dpiScale; }

    // Geometry Builders (Exact match to openpixel.com/assets/island.js)
    ID2D1PathGeometry* CreateIslandOrNotchPath(
        float left, float top, float width, float height,
        float cornerRadius, float notchFactor01, EdgePlacement edge);

    ID2D1PathGeometry* CreateBottomTaskbarNotchPath(
        float left, float top, float width, float height,
        float cornerRadius, float flareRadius);

    // Liquid Glass & Candy Glass Live Desktop Blur + Lens Refraction
    // Returns true if glass bitmap was rendered; falls back safely to Black on failure
    bool RenderLiquidGlassBackdrop(
        HWND hwnd, const RECT& screenRect,
        float dipX, float dipY, float dipW, float dipH,
        float blurRadiusPx, bool isCandyGlass);

    // Soft Theme Particle Ambience (Rosé hearts, Lavender stars, Peach bokeh, Sakura petals)
    void RenderThemeAmbience(
        SurfaceTheme theme, float left, float top, float width, float height, float timeSec);

    // AlbumTint Full-Bleed Top-Edge Saturated Backdrop & Cover Glow
    void RenderAlbumBackdrop(
        const MediaSessionState& media, float left, float top, float width, float height);

    // Typography & Drawing Helpers (Whole-Pixel Snapped)
    void FillRoundedRect(const D2D1_RECT_F& r, float radius, const D2D1_COLOR_F& color);
    void DrawRoundedRect(const D2D1_RECT_F& r, float radius, const D2D1_COLOR_F& color, float strokeWidth = 1.0f);
    void FillCircle(float cx, float cy, float radius, const D2D1_COLOR_F& color);
    void DrawCircle(float cx, float cy, float radius, const D2D1_COLOR_F& color, float strokeWidth = 1.5f);
    void DrawLine(float x1, float y1, float x2, float y2, const D2D1_COLOR_F& color, float strokeWidth = 1.5f);

    void DrawTextStr(
        const std::wstring& text, const D2D1_RECT_F& rect,
        float fontSizeDip, int fontWeight, const D2D1_COLOR_F& color,
        bool mono = false,
        DWRITE_TEXT_ALIGNMENT hAlign = DWRITE_TEXT_ALIGNMENT_LEADING,
        DWRITE_PARAGRAPH_ALIGNMENT vAlign = DWRITE_PARAGRAPH_ALIGNMENT_CENTER,
        bool noWrap = true);

    // Crisp Vector Icon Renderer (Lucide Icons matching icons.js)
    void DrawLucideIcon(
        const std::string& iconName, float cx, float cy, float sizeDip,
        const D2D1_COLOR_F& color, float strokeWidth = 1.75f);

    // Built-in QR Matrix Renderer for Send-to-Phone & PC Deck Pairing
    void DrawQrCode(
        const std::wstring& payload, float left, float top, float sizeDip,
        const D2D1_COLOR_F& fgColor, const D2D1_COLOR_F& bgColor);

    // Hit Region Registration
    void ClearHitRegions() { m_hitRegions.clear(); }
    void AddHitRegion(const std::string& id, const D2D1_RECT_F& r,
                      std::function<void()> onClick,
                      std::function<void(float)> onDrag = nullptr);
    bool DispatchClick(float dipX, float dipY);
    bool DispatchDrag(float dipX, float dipY);
    bool IsHovered(const D2D1_RECT_F& r, float dipX, float dipY) const;

private:
    ID2D1Factory* m_d2dFactory = nullptr;
    IDWriteFactory* m_dwriteFactory = nullptr;
    ID2D1DCRenderTarget* m_dcRenderTarget = nullptr;
    ID2D1SolidColorBrush* m_solidBrush = nullptr;

    HDC m_memDc = nullptr;
    HBITMAP m_dibBitmap = nullptr;
    HBITMAP m_oldBitmap = nullptr;
    uint32_t* m_dibPixels = nullptr;
    int m_surfaceW = 0;
    int m_surfaceH = 0;
    float m_dpiScale = 1.0f;
    bool m_softwareMode = false;

    // Static-background cache for 0% CPU Liquid Glass when wallpaper/windows behind island are static
    uint64_t m_cachedBackdropHash = 0;
    int m_cachedGlassW = 0;
    int m_cachedGlassH = 0;
    float m_cachedBlurRadius = -1.0f;
    std::vector<uint32_t> m_cachedBlurredPixels;

    std::vector<HitRegion> m_hitRegions;
    std::string m_activeDragId;
    std::map<uint64_t, IDWriteTextFormat*> m_textFormatCache;
};

} // namespace np
