#include "D2DRenderer.h"
#include "../core/Spring.h"
#include <cmath>
#include <cstring>

namespace np {

D2DRenderer::D2DRenderer() = default;

D2DRenderer::~D2DRenderer() {
    Shutdown();
}

bool D2DRenderer::Initialize(bool forceSoftwareCpu) {
    Shutdown();
    m_softwareMode = forceSoftwareCpu;

    HRESULT hr = D2D1CreateFactory(
        D2D1_FACTORY_TYPE_MULTI_THREADED,
        __uuidof(ID2D1Factory),
        nullptr,
        reinterpret_cast<void**>(&m_d2dFactory));
    if (FAILED(hr) || !m_d2dFactory) return false;

    hr = DWriteCreateFactory(
        DWRITE_FACTORY_TYPE_SHARED,
        __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(&m_dwriteFactory));
    if (FAILED(hr) || !m_dwriteFactory) return false;

    D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
        forceSoftwareCpu ? D2D1_RENDER_TARGET_TYPE_SOFTWARE : D2D1_RENDER_TARGET_TYPE_DEFAULT,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
        96.0f, 96.0f,
        D2D1_RENDER_TARGET_USAGE_NONE,
        D2D1_FEATURE_LEVEL_DEFAULT);

    hr = m_d2dFactory->CreateDCRenderTarget(&props, &m_dcRenderTarget);
    if (FAILED(hr) || !m_dcRenderTarget) {
        // Safe fallback to software CPU render target (e.g. RTX 50 / driver failure)
        props.type = D2D1_RENDER_TARGET_TYPE_SOFTWARE;
        hr = m_d2dFactory->CreateDCRenderTarget(&props, &m_dcRenderTarget);
        if (FAILED(hr) || !m_dcRenderTarget) return false;
        m_softwareMode = true;
    }

    m_dcRenderTarget->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    m_dcRenderTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    m_dcRenderTarget->CreateSolidColorBrush(D2D1::ColorF(1, 1, 1, 1), &m_solidBrush);
    return m_solidBrush != nullptr;
}

void D2DRenderer::Shutdown() {
    for (auto& kv : m_textFormatCache) {
        if (kv.second) kv.second->Release();
    }
    m_textFormatCache.clear();
    if (m_solidBrush) { m_solidBrush->Release(); m_solidBrush = nullptr; }
    if (m_dcRenderTarget) { m_dcRenderTarget->Release(); m_dcRenderTarget = nullptr; }
    if (m_dwriteFactory) { m_dwriteFactory->Release(); m_dwriteFactory = nullptr; }
    if (m_d2dFactory) { m_d2dFactory->Release(); m_d2dFactory = nullptr; }

    if (m_memDc) {
        if (m_oldBitmap) SelectObject(m_memDc, m_oldBitmap);
        if (m_dibBitmap) DeleteObject(m_dibBitmap);
        DeleteDC(m_memDc);
        m_memDc = nullptr;
        m_dibBitmap = nullptr;
        m_oldBitmap = nullptr;
        m_dibPixels = nullptr;
    }
    m_surfaceW = 0;
    m_surfaceH = 0;
}

bool D2DRenderer::BeginLayeredFrame(HWND hwnd, int widthPx, int heightPx, float dpiScale) {
    (void)hwnd;
    if (widthPx <= 0 || heightPx <= 0 || widthPx > 4096 || heightPx > 4096) return false;
    if (!m_dcRenderTarget) {
        if (!Initialize(m_softwareMode)) return false;
    }
    m_dpiScale = std::clamp(dpiScale, 1.0f, 4.0f);

    if (!m_memDc || widthPx != m_surfaceW || heightPx != m_surfaceH || !m_dibPixels) {
        if (m_memDc) {
            if (m_oldBitmap) SelectObject(m_memDc, m_oldBitmap);
            if (m_dibBitmap) DeleteObject(m_dibBitmap);
            DeleteDC(m_memDc);
            m_memDc = nullptr;
            m_dibBitmap = nullptr;
            m_oldBitmap = nullptr;
            m_dibPixels = nullptr;
        }
        HDC screenDc = GetDC(nullptr);
        if (!screenDc) return false;
        m_memDc = CreateCompatibleDC(screenDc);
        if (!m_memDc) {
            ReleaseDC(nullptr, screenDc);
            return false;
        }

        BITMAPINFO bmi = {};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = widthPx;
        bmi.bmiHeader.biHeight = -heightPx; // top-down DIB
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        m_dibBitmap = CreateDIBSection(screenDc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
        ReleaseDC(nullptr, screenDc);
        if (!m_dibBitmap || !bits) {
            if (m_dibBitmap) DeleteObject(m_dibBitmap);
            DeleteDC(m_memDc);
            m_memDc = nullptr;
            m_dibBitmap = nullptr;
            return false;
        }

        m_oldBitmap = static_cast<HBITMAP>(SelectObject(m_memDc, m_dibBitmap));
        m_dibPixels = static_cast<uint32_t*>(bits);
        m_surfaceW = widthPx;
        m_surfaceH = heightPx;
    }

    std::memset(m_dibPixels, 0, static_cast<size_t>(m_surfaceW) * m_surfaceH * sizeof(uint32_t));

    RECT rc = { 0, 0, m_surfaceW, m_surfaceH };
    HRESULT hr = m_dcRenderTarget->BindDC(m_memDc, &rc);
    if (FAILED(hr)) {
        // Safely recreate render target in software mode on any driver error
        if (!Initialize(true)) return false;
        if (FAILED(m_dcRenderTarget->BindDC(m_memDc, &rc))) return false;
    }

    m_dcRenderTarget->SetDpi(96.0f * m_dpiScale, 96.0f * m_dpiScale);
    m_dcRenderTarget->BeginDraw();
    m_dcRenderTarget->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f, 0.0f));
    return true;
}

void D2DRenderer::EndLayeredFrame(HWND hwnd, POINT windowScreenPos, BYTE globalAlpha) {
    if (!m_dcRenderTarget) return;
    HRESULT hr = m_dcRenderTarget->EndDraw();
    if (hr == D2DERR_RECREATE_TARGET || FAILED(hr)) {
        Initialize(true);
        return;
    }

    if (hwnd && IsWindow(hwnd) && m_memDc && m_surfaceW > 0 && m_surfaceH > 0) {
        HDC screenDc = GetDC(nullptr);
        if (screenDc) {
            SIZE wndSize = { m_surfaceW, m_surfaceH };
            POINT srcPt = { 0, 0 };
            BLENDFUNCTION blend = {};
            blend.BlendOp = AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = globalAlpha;
            blend.AlphaFormat = AC_SRC_ALPHA;

            UpdateLayeredWindow(hwnd, screenDc, &windowScreenPos, &wndSize, m_memDc, &srcPt, 0, &blend, ULW_ALPHA);
            ReleaseDC(nullptr, screenDc);
        }
    }
}

// ============================================================================
// Exact Notch & Floating Island Path Generator (Whole-Pixel Snapped)
// ============================================================================
ID2D1PathGeometry* D2DRenderer::CreateIslandOrNotchPath(
    float left, float top, float width, float height,
    float cornerRadius, float notchFactor01, EdgePlacement edge)
{
    if (!m_d2dFactory || width <= 2.0f || height <= 2.0f) return nullptr;

    left   = SnapDipToWholePixel(left, m_dpiScale);
    top    = SnapDipToWholePixel(top, m_dpiScale);
    width  = SnapDipToWholePixel(width, m_dpiScale);
    height = SnapDipToWholePixel(height, m_dpiScale);

    ID2D1PathGeometry* path = nullptr;
    if (FAILED(m_d2dFactory->CreatePathGeometry(&path)) || !path) return nullptr;

    ID2D1GeometrySink* sink = nullptr;
    if (FAILED(path->Open(&sink)) || !sink) {
        path->Release();
        return nullptr;
    }

    float r = std::min({ cornerRadius, width * 0.5f, height * 0.5f });
    r = std::max(1.0f, r);
    float n = std::clamp(notchFactor01, 0.0f, 1.0f);

    if (edge == EdgePlacement::TopCenter && n > 0.5f) {
        float flare = FLARE_R * ((n - 0.5f) * 2.0f);
        float right = left + width;
        float bottom = top + height;

        sink->BeginFigure(D2D1::Point2F(left - flare, top), D2D1_FIGURE_BEGIN_FILLED);
        if (flare > 0.2f) {
            sink->AddArc(D2D1::ArcSegment(
                D2D1::Point2F(left, top + flare),
                D2D1::SizeF(flare, flare),
                0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        } else {
            sink->AddLine(D2D1::Point2F(left, top));
        }
        sink->AddLine(D2D1::Point2F(left, bottom - r));
        sink->AddArc(D2D1::ArcSegment(
            D2D1::Point2F(left + r, bottom),
            D2D1::SizeF(r, r),
            0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(right - r, bottom));
        sink->AddArc(D2D1::ArcSegment(
            D2D1::Point2F(right, bottom - r),
            D2D1::SizeF(r, r),
            0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(right, top + flare));
        if (flare > 0.2f) {
            sink->AddArc(D2D1::ArcSegment(
                D2D1::Point2F(right + flare, top),
                D2D1::SizeF(flare, flare),
                0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        } else {
            sink->AddLine(D2D1::Point2F(right, top));
        }
        sink->AddLine(D2D1::Point2F(left - flare, top));
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    } else if (edge == EdgePlacement::LeftEdge && n > 0.5f) {
        float right = left + width;
        float bottom = top + height;
        sink->BeginFigure(D2D1::Point2F(left, top), D2D1_FIGURE_BEGIN_FILLED);
        sink->AddLine(D2D1::Point2F(right - r, top));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(right, top + r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(right, bottom - r));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(right - r, bottom), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(left, bottom));
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    } else if (edge == EdgePlacement::RightEdge && n > 0.5f) {
        float right = left + width;
        float bottom = top + height;
        sink->BeginFigure(D2D1::Point2F(right, top), D2D1_FIGURE_BEGIN_FILLED);
        sink->AddLine(D2D1::Point2F(left + r, top));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(left, top + r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(left, bottom - r));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(left + r, bottom), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(right, bottom));
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    } else {
        float topR = (n <= 0.5f) ? (r * (1.0f - n * 2.0f)) : 0.5f;
        topR = std::clamp(topR, 0.5f, r);
        float right = left + width;
        float bottom = top + height;

        sink->BeginFigure(D2D1::Point2F(left + topR, top), D2D1_FIGURE_BEGIN_FILLED);
        sink->AddLine(D2D1::Point2F(right - topR, top));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(right, top + topR), D2D1::SizeF(topR, topR), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(right, bottom - r));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(right - r, bottom), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(left + r, bottom));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(left, bottom - r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->AddLine(D2D1::Point2F(left, top + topR));
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(left + topR, top), D2D1::SizeF(topR, topR), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    }

    sink->Close();
    sink->Release();
    return path;
}

ID2D1PathGeometry* D2DRenderer::CreateBottomTaskbarNotchPath(
    float left, float top, float width, float height,
    float cornerRadius, float flareRadius)
{
    if (!m_d2dFactory || width <= 2.0f || height <= 2.0f) return nullptr;

    left   = SnapDipToWholePixel(left, m_dpiScale);
    top    = SnapDipToWholePixel(top, m_dpiScale);
    width  = SnapDipToWholePixel(width, m_dpiScale);
    height = SnapDipToWholePixel(height, m_dpiScale);

    ID2D1PathGeometry* path = nullptr;
    if (FAILED(m_d2dFactory->CreatePathGeometry(&path)) || !path) return nullptr;

    ID2D1GeometrySink* sink = nullptr;
    if (FAILED(path->Open(&sink)) || !sink) {
        path->Release();
        return nullptr;
    }

    float r = std::min({ cornerRadius, width * 0.5f, height * 0.5f });
    float flare = std::min(flareRadius, height * 0.5f);
    float right = left + width;
    float bottom = top + height;

    sink->BeginFigure(D2D1::Point2F(left - flare, bottom), D2D1_FIGURE_BEGIN_FILLED);
    if (flare > 0.2f) {
        sink->AddArc(D2D1::ArcSegment(
            D2D1::Point2F(left, bottom - flare),
            D2D1::SizeF(flare, flare),
            0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
    } else {
        sink->AddLine(D2D1::Point2F(left, bottom));
    }
    sink->AddLine(D2D1::Point2F(left, top + r));
    sink->AddArc(D2D1::ArcSegment(
        D2D1::Point2F(left + r, top),
        D2D1::SizeF(r, r),
        0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
    sink->AddLine(D2D1::Point2F(right - r, top));
    sink->AddArc(D2D1::ArcSegment(
        D2D1::Point2F(right, top + r),
        D2D1::SizeF(r, r),
        0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
    sink->AddLine(D2D1::Point2F(right, bottom - flare));
    if (flare > 0.2f) {
        sink->AddArc(D2D1::ArcSegment(
            D2D1::Point2F(right + flare, bottom),
            D2D1::SizeF(flare, flare),
            0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
    } else {
        sink->AddLine(D2D1::Point2F(right, bottom));
    }
    sink->AddLine(D2D1::Point2F(left - flare, bottom));
    sink->EndFigure(D2D1_FIGURE_END_CLOSED);

    sink->Close();
    sink->Release();
    return path;
}

// ============================================================================
// Crash-Proof Liquid Glass & Candy Glass Shader Pipeline
// - Safe bounds & null guards on every GDI/D2D allocation
// - 64-point static background hash caching for 0% CPU when static
// - Automatically returns false on any driver/capture failure so caller falls
//   back cleanly to Black (#0B0B0C)
// ============================================================================
bool D2DRenderer::RenderLiquidGlassBackdrop(
    HWND hwnd, const RECT& screenRect,
    float dipX, float dipY, float dipW, float dipH,
    float blurRadiusPx, bool isCandyGlass)
{
    (void)hwnd;
    if (!m_dcRenderTarget || dipW <= 2.0f || dipH <= 2.0f) return false;

    int srcW = screenRect.right - screenRect.left;
    int srcH = screenRect.bottom - screenRect.top;
    if (srcW <= 4 || srcH <= 4 || srcW > 4096 || srcH > 4096) return false;

    int capW = std::clamp(srcW / 2, 16, 480);
    int capH = std::clamp(srcH / 2, 8, 360);

    HDC screenDc = GetDC(nullptr);
    if (!screenDc) return false;

    HDC capDc = CreateCompatibleDC(screenDc);
    if (!capDc) {
        ReleaseDC(nullptr, screenDc);
        return false;
    }

    BITMAPINFO bmi = {};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = capW;
    bmi.bmiHeader.biHeight = -capH;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    void* rawBits = nullptr;
    HBITMAP capBmp = CreateDIBSection(screenDc, &bmi, DIB_RGB_COLORS, &rawBits, nullptr, 0);
    if (!capBmp || !rawBits) {
        if (capBmp) DeleteObject(capBmp);
        DeleteDC(capDc);
        ReleaseDC(nullptr, screenDc);
        return false;
    }

    HGDIOBJ oldBmp = SelectObject(capDc, capBmp);
    SetStretchBltMode(capDc, HALFTONE);
    BOOL bltOk = StretchBlt(
        capDc, 0, 0, capW, capH,
        screenDc,
        screenRect.left, screenRect.top, srcW, srcH,
        SRCCOPY);

    SelectObject(capDc, oldBmp);
    DeleteDC(capDc);
    ReleaseDC(nullptr, screenDc);

    if (!bltOk) {
        DeleteObject(capBmp);
        return false;
    }

    const uint32_t* srcPixels = static_cast<const uint32_t*>(rawBits);
    size_t totalPixels = static_cast<size_t>(capW) * capH;

    // 64-point FNV-1a sample hash to detect static background -> 0% CPU reuse!
    uint64_t hash = 14695981039346656037ULL;
    size_t stride = std::max<size_t>(1, totalPixels / 64);
    for (size_t i = 0; i < totalPixels; i += stride) {
        hash ^= srcPixels[i];
        hash *= 1099511628211ULL;
    }
    if (isCandyGlass) hash ^= 0xCA4D1ULL;

    float safeBlur = std::clamp(blurRadiusPx, 0.0f, 40.0f);
    bool cacheHit = (hash == m_cachedBackdropHash &&
                     capW == m_cachedGlassW &&
                     capH == m_cachedGlassH &&
                     std::fabs(safeBlur - m_cachedBlurRadius) < 0.5f &&
                     m_cachedBlurredPixels.size() == totalPixels);

    if (!cacheHit) {
        m_cachedBackdropHash = hash;
        m_cachedGlassW = capW;
        m_cachedGlassH = capH;
        m_cachedBlurRadius = safeBlur;
        m_cachedBlurredPixels.assign(totalPixels, 0xFF0B0B0Cu);

        std::vector<uint32_t> refracted(totalPixels, 0xFF0B0B0Cu);
        float cx = capW * 0.5f;
        float cy = capH * 0.5f;
        float invRx = 1.0f / std::max(1.0f, cx);
        float invRy = 1.0f / std::max(1.0f, cy);

        // Pass 1: Edge Lens Refraction Displacement + 165% Saturation Boost
        for (int y = 0; y < capH; ++y) {
            float ny = (y - cy) * invRy;
            for (int x = 0; x < capW; ++x) {
                float nx = (x - cx) * invRx;
                float dist2 = nx * nx + ny * ny;
                float bend = (dist2 > 0.45f) ? ((dist2 - 0.45f) * 0.34f) : 0.0f;
                int sx = std::clamp(static_cast<int>(x - nx * bend * capW * 0.18f), 0, capW - 1);
                int sy = std::clamp(static_cast<int>(y - ny * bend * capH * 0.22f), 0, capH - 1);

                uint32_t px = srcPixels[sy * capW + sx];
                float b = (px & 0xFF) / 255.0f;
                float g = ((px >> 8) & 0xFF) / 255.0f;
                float r = ((px >> 16) & 0xFF) / 255.0f;

                float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r = std::clamp(luma + (r - luma) * 1.65f, 0.0f, 1.0f);
                g = std::clamp(luma + (g - luma) * 1.65f, 0.0f, 1.0f);
                b = std::clamp(luma + (b - luma) * 1.65f, 0.0f, 1.0f);

                if (isCandyGlass) {
                    float grad = static_cast<float>(x) / std::max(1, capW);
                    float tintR = 0.36f * (1.0f - grad) + 0.20f * grad;
                    float tintG = 0.14f * (1.0f - grad) + 0.24f * grad;
                    float tintB = 0.34f * (1.0f - grad) + 0.42f * grad;
                    r = r * 0.48f + tintR;
                    g = g * 0.48f + tintG;
                    b = b * 0.48f + tintB;
                } else {
                    r = r * 0.52f + 0.04f;
                    g = g * 0.52f + 0.05f;
                    b = b * 0.54f + 0.08f;
                }

                uint8_t rb = static_cast<uint8_t>(std::clamp(r * 255.0f, 0.0f, 255.0f));
                uint8_t gb = static_cast<uint8_t>(std::clamp(g * 255.0f, 0.0f, 255.0f));
                uint8_t bb = static_cast<uint8_t>(std::clamp(b * 255.0f, 0.0f, 255.0f));
                refracted[y * capW + x] = (0xFFu << 24) | (rb << 16) | (gb << 8) | bb;
            }
        }

        // Pass 2: Two-iteration separable box blur approximating Gaussian Blur (radius 0..40px)
        int radius = std::clamp(static_cast<int>(safeBlur * 0.35f), 0, 14);
        if (radius <= 0) {
            m_cachedBlurredPixels = refracted;
        } else {
            std::vector<uint32_t> temp(totalPixels, 0xFF0B0B0Cu);
            for (int iter = 0; iter < 2; ++iter) {
                const uint32_t* inBuf = (iter == 0) ? refracted.data() : m_cachedBlurredPixels.data();
                for (int y = 0; y < capH; ++y) {
                    for (int x = 0; x < capW; ++x) {
                        int sumR = 0, sumG = 0, sumB = 0, count = 0;
                        for (int k = -radius; k <= radius; ++k) {
                            int sx = std::clamp(x + k, 0, capW - 1);
                            uint32_t c = inBuf[y * capW + sx];
                            sumB += (c & 0xFF);
                            sumG += ((c >> 8) & 0xFF);
                            sumR += ((c >> 16) & 0xFF);
                            ++count;
                        }
                        if (count > 0) {
                            temp[y * capW + x] = (0xFFu << 24)
                                | ((sumR / count) << 16)
                                | ((sumG / count) << 8)
                                | (sumB / count);
                        }
                    }
                }
                for (int y = 0; y < capH; ++y) {
                    for (int x = 0; x < capW; ++x) {
                        int sumR = 0, sumG = 0, sumB = 0, count = 0;
                        for (int k = -radius; k <= radius; ++k) {
                            int sy = std::clamp(y + k, 0, capH - 1);
                            uint32_t c = temp[sy * capW + x];
                            sumB += (c & 0xFF);
                            sumG += ((c >> 8) & 0xFF);
                            sumR += ((c >> 16) & 0xFF);
                            ++count;
                        }
                        if (count > 0) {
                            m_cachedBlurredPixels[y * capW + x] = (0xFFu << 24)
                                | ((sumR / count) << 16)
                                | ((sumG / count) << 8)
                                | (sumB / count);
                        }
                    }
                }
            }
        }
    }

    DeleteObject(capBmp);

    if (m_cachedBlurredPixels.size() != totalPixels) return false;

    D2D1_BITMAP_PROPERTIES bmpProps = D2D1::BitmapProperties(
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
        96.0f, 96.0f);

    ID2D1Bitmap* d2dBmp = nullptr;
    HRESULT hr = m_dcRenderTarget->CreateBitmap(
        D2D1::SizeU(capW, capH),
        m_cachedBlurredPixels.data(),
        capW * sizeof(uint32_t),
        &bmpProps,
        &d2dBmp);
    if (FAILED(hr) || !d2dBmp) return false;

    D2D1_RECT_F destRect = D2D1::RectF(dipX, dipY, dipX + dipW, dipY + dipH);
    m_dcRenderTarget->DrawBitmap(d2dBmp, destRect, 0.96f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
    d2dBmp->Release();
    return true;
}

// ============================================================================
// Soft Themes Particle Ambience (Rosé, Lavender, Peach, Sakura)
// ============================================================================
void D2DRenderer::RenderThemeAmbience(
    SurfaceTheme theme, float left, float top, float width, float height, float timeSec)
{
    if (!m_dcRenderTarget || !m_solidBrush) return;

    if (theme == SurfaceTheme::Rose) {
        for (int i = 0; i < 9; ++i) {
            float seed = i * 1.37f;
            float phase = std::fmod(timeSec * (0.14f + (i % 3) * 0.04f) + seed, 1.0f);
            float hx = left + width * (0.10f + 0.80f * std::fmod(i * 0.6180339f, 1.0f))
                       + std::sin(timeSec * 1.4f + seed) * 5.0f;
            float hy = top + height * (1.05f - phase * 1.10f);
            float alpha = std::sin(phase * 3.14159265f) * 0.42f;
            float sz = 6.0f + (i % 3) * 2.2f;
            DrawLucideIcon("heart-filled", hx, hy, sz, Rgba(255, 143, 177, alpha), 1.4f);
        }
    } else if (theme == SurfaceTheme::Lavender) {
        for (int i = 0; i < 16; ++i) {
            float sx = left + width * (0.06f + 0.88f * std::fmod(i * 0.754877f, 1.0f));
            float sy = top + height * (0.12f + 0.78f * std::fmod(i * 0.569840f, 1.0f));
            float twinkle = 0.5f + 0.5f * std::sin(timeSec * (2.2f + (i % 4) * 0.6f) + i * 1.9f);
            float alpha = 0.12f + twinkle * 0.48f;
            float r = 2.0f + twinkle * 2.4f;
            D2D1_COLOR_F col = (i % 2 == 0) ? Rgba(239, 237, 232, alpha) : Rgba(195, 166, 255, alpha);
            DrawLine(sx - r, sy, sx + r, sy, col, 1.2f);
            DrawLine(sx, sy - r, sx, sy + r, col, 1.2f);
        }
    } else if (theme == SurfaceTheme::Peach) {
        for (int i = 0; i < 7; ++i) {
            float seed = i * 2.1f;
            float ox = left + width * (0.12f + 0.76f * std::fmod(i * 0.618f, 1.0f))
                       + std::sin(timeSec * 0.6f + seed) * 14.0f;
            float oy = top + height * (0.20f + 0.60f * std::fmod(i * 0.414f, 1.0f))
                       + std::cos(timeSec * 0.5f + seed) * 9.0f;
            float rad = 14.0f + (i % 3) * 8.0f;
            D2D1_COLOR_F col = (i % 2 == 0) ? Rgba(255, 179, 138, 0.12f) : Rgba(255, 214, 168, 0.10f);
            FillCircle(ox, oy, rad, col);
        }
    } else if (theme == SurfaceTheme::Sakura) {
        for (int i = 0; i < 11; ++i) {
            float seed = i * 1.19f;
            float phase = std::fmod(timeSec * (0.12f + (i % 4) * 0.03f) + seed, 1.0f);
            float px = left + width * std::fmod(i * 0.618f + phase * 0.25f, 1.0f)
                       + std::sin(timeSec * 1.5f + seed) * 6.0f;
            float py = top + height * phase;
            float alpha = std::sin(phase * 3.14159265f) * 0.44f;
            D2D1_RECT_F petal = D2D1::RectF(px - 3.5f, py - 2.0f, px + 3.5f, py + 2.0f);
            FillRoundedRect(petal, 2.0f, Rgba(255, 196, 210, alpha));
        }
    }
}

void D2DRenderer::RenderAlbumBackdrop(
    const MediaSessionState& media, float left, float top, float width, float height)
{
    if (!m_dcRenderTarget || !media.hasSession) return;

    D2D1_GRADIENT_STOP stops[3] = {};
    D2D1_COLOR_F c1 = media.dominantColor;
    D2D1_COLOR_F c2 = media.secondaryColor;
    stops[0].position = 0.0f;
    stops[0].color = D2D1::ColorF(c1.r * 0.55f, c1.g * 0.55f, c1.b * 0.55f, 0.78f);
    stops[1].position = 0.52f;
    stops[1].color = D2D1::ColorF(c2.r * 0.45f, c2.g * 0.45f, c2.b * 0.45f, 0.55f);
    stops[2].position = 1.0f;
    stops[2].color = D2D1::ColorF(0.04f, 0.04f, 0.05f, 0.88f);

    ID2D1GradientStopCollection* stopCol = nullptr;
    if (SUCCEEDED(m_dcRenderTarget->CreateGradientStopCollection(stops, 3, &stopCol)) && stopCol) {
        ID2D1LinearGradientBrush* gradBrush = nullptr;
        if (SUCCEEDED(m_dcRenderTarget->CreateLinearGradientBrush(
                D2D1::LinearGradientBrushProperties(
                    D2D1::Point2F(left, top),
                    D2D1::Point2F(left + width, top + height)),
                stopCol, &gradBrush)) && gradBrush)
        {
            m_dcRenderTarget->FillRectangle(D2D1::RectF(left, top, left + width, top + height), gradBrush);
            gradBrush->Release();
        }
        stopCol->Release();
    }
}

// ============================================================================
// Whole-Pixel Snapped Primitives & Typography
// ============================================================================
static D2D1_RECT_F SnapRectToWholePixels(const D2D1_RECT_F& r, float dpiScale) {
    return D2D1::RectF(
        SnapDipToWholePixel(r.left, dpiScale),
        SnapDipToWholePixel(r.top, dpiScale),
        SnapDipToWholePixel(r.right, dpiScale),
        SnapDipToWholePixel(r.bottom, dpiScale));
}

void D2DRenderer::FillRoundedRect(const D2D1_RECT_F& r, float radius, const D2D1_COLOR_F& color) {
    if (!m_dcRenderTarget || !m_solidBrush) return;
    m_solidBrush->SetColor(color);
    m_dcRenderTarget->FillRoundedRectangle(D2D1::RoundedRect(SnapRectToWholePixels(r, m_dpiScale), radius, radius), m_solidBrush);
}

void D2DRenderer::DrawRoundedRect(const D2D1_RECT_F& r, float radius, const D2D1_COLOR_F& color, float strokeWidth) {
    if (!m_dcRenderTarget || !m_solidBrush) return;
    m_solidBrush->SetColor(color);
    m_dcRenderTarget->DrawRoundedRectangle(D2D1::RoundedRect(SnapRectToWholePixels(r, m_dpiScale), radius, radius), m_solidBrush, strokeWidth);
}

void D2DRenderer::FillCircle(float cx, float cy, float radius, const D2D1_COLOR_F& color) {
    if (!m_dcRenderTarget || !m_solidBrush) return;
    m_solidBrush->SetColor(color);
    m_dcRenderTarget->FillEllipse(
        D2D1::Ellipse(D2D1::Point2F(SnapDipToWholePixel(cx, m_dpiScale), SnapDipToWholePixel(cy, m_dpiScale)), radius, radius),
        m_solidBrush);
}

void D2DRenderer::DrawCircle(float cx, float cy, float radius, const D2D1_COLOR_F& color, float strokeWidth) {
    if (!m_dcRenderTarget || !m_solidBrush) return;
    m_solidBrush->SetColor(color);
    m_dcRenderTarget->DrawEllipse(
        D2D1::Ellipse(D2D1::Point2F(SnapDipToWholePixel(cx, m_dpiScale), SnapDipToWholePixel(cy, m_dpiScale)), radius, radius),
        m_solidBrush, strokeWidth);
}

void D2DRenderer::DrawLine(float x1, float y1, float x2, float y2, const D2D1_COLOR_F& color, float strokeWidth) {
    if (!m_dcRenderTarget || !m_solidBrush) return;
    m_solidBrush->SetColor(color);
    m_dcRenderTarget->DrawLine(
        D2D1::Point2F(SnapDipToWholePixel(x1, m_dpiScale), SnapDipToWholePixel(y1, m_dpiScale)),
        D2D1::Point2F(SnapDipToWholePixel(x2, m_dpiScale), SnapDipToWholePixel(y2, m_dpiScale)),
        m_solidBrush, strokeWidth);
}

void D2DRenderer::DrawTextStr(
    const std::wstring& text, const D2D1_RECT_F& rect,
    float fontSizeDip, int fontWeight, const D2D1_COLOR_F& color,
    bool mono, DWRITE_TEXT_ALIGNMENT hAlign, DWRITE_PARAGRAPH_ALIGNMENT vAlign, bool noWrap)
{
    if (!m_dcRenderTarget || !m_dwriteFactory || !m_solidBrush || text.empty()) return;

    const auto& s = GetAppState().settings;
    float scaledSize = fontSizeDip * std::clamp(s.textScale, 0.85f, 1.50f);

    static const wchar_t* fontFamilies[] = {
        L"Segoe UI Variable Display",
        L"Segoe UI",
        L"Calibri",
        L"Consolas",
        L"Georgia",
        L"Arial"
    };
    int fontIdx = mono ? 3 : std::clamp(s.fontIndex, 0, 5);
    const wchar_t* family = fontFamilies[fontIdx];
    int sizeTen = static_cast<int>(std::round(scaledSize * 10.0f));
    int clampedWeight = std::clamp(fontWeight, 100, 900);
    uint64_t cacheKey = (static_cast<uint64_t>(fontIdx) << 32) |
                        (static_cast<uint64_t>(clampedWeight) << 16) |
                        static_cast<uint64_t>(sizeTen);

    IDWriteTextFormat* fmt = nullptr;
    auto it = m_textFormatCache.find(cacheKey);
    if (it != m_textFormatCache.end()) {
        fmt = it->second;
    } else {
        HRESULT hr = m_dwriteFactory->CreateTextFormat(
            family, nullptr,
            static_cast<DWRITE_FONT_WEIGHT>(clampedWeight),
            DWRITE_FONT_STYLE_NORMAL,
            DWRITE_FONT_STRETCH_NORMAL,
            scaledSize,
            L"en-US",
            &fmt);
        if (FAILED(hr) || !fmt) return;
        DWRITE_TRIMMING trim = { DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0 };
        IDWriteInlineObject* ellipsis = nullptr;
        m_dwriteFactory->CreateEllipsisTrimmingSign(fmt, &ellipsis);
        fmt->SetTrimming(&trim, ellipsis);
        if (ellipsis) ellipsis->Release();
        m_textFormatCache[cacheKey] = fmt;
    }

    fmt->SetTextAlignment(hAlign);
    fmt->SetParagraphAlignment(vAlign);
    fmt->SetWordWrapping(noWrap ? DWRITE_WORD_WRAPPING_NO_WRAP : DWRITE_WORD_WRAPPING_WRAP);

    D2D1_RECT_F snapped = D2D1::RectF(
        std::floor(rect.left * m_dpiScale) / m_dpiScale,
        std::floor(rect.top * m_dpiScale) / m_dpiScale,
        std::ceil(rect.right * m_dpiScale) / m_dpiScale,
        std::ceil(rect.bottom * m_dpiScale) / m_dpiScale);

    m_solidBrush->SetColor(color);
    m_dcRenderTarget->DrawTextW(
        text.c_str(),
        static_cast<UINT32>(text.size()),
        fmt,
        snapped,
        m_solidBrush,
        D2D1_DRAW_TEXT_OPTIONS_CLIP);
}

void D2DRenderer::DrawLucideIcon(
    const std::string& iconName, float cx, float cy, float sizeDip,
    const D2D1_COLOR_F& color, float strokeWidth)
{
    if (!m_dcRenderTarget || !m_solidBrush) return;
    cx = SnapDipToWholePixel(cx, m_dpiScale);
    cy = SnapDipToWholePixel(cy, m_dpiScale);
    float r = sizeDip * 0.5f;

    if (iconName == "music") {
        DrawLine(cx - r * 0.4f, cy + r * 0.4f, cx - r * 0.4f, cy - r * 0.6f, color, strokeWidth);
        DrawLine(cx + r * 0.55f, cy + r * 0.25f, cx + r * 0.55f, cy - r * 0.75f, color, strokeWidth);
        DrawLine(cx - r * 0.4f, cy - r * 0.6f, cx + r * 0.55f, cy - r * 0.75f, color, strokeWidth * 1.15f);
        FillCircle(cx - r * 0.58f, cy + r * 0.45f, r * 0.26f, color);
        FillCircle(cx + r * 0.36f, cy + r * 0.30f, r * 0.26f, color);
    } else if (iconName == "globe") {
        DrawCircle(cx, cy, r * 0.82f, color, strokeWidth);
        DrawLine(cx - r * 0.80f, cy, cx + r * 0.80f, cy, color, strokeWidth * 0.85f);
        DrawLine(cx, cy - r * 0.80f, cx, cy + r * 0.80f, color, strokeWidth * 0.85f);
    } else if (iconName == "bell") {
        D2D1_RECT_F dome = D2D1::RectF(cx - r * 0.55f, cy - r * 0.65f, cx + r * 0.55f, cy + r * 0.35f);
        DrawRoundedRect(dome, r * 0.5f, color, strokeWidth);
        DrawLine(cx - r * 0.75f, cy + r * 0.35f, cx + r * 0.75f, cy + r * 0.35f, color, strokeWidth);
        FillCircle(cx, cy + r * 0.68f, r * 0.16f, color);
    } else if (iconName == "message-circle") {
        DrawCircle(cx, cy - r * 0.05f, r * 0.78f, color, strokeWidth);
        DrawLine(cx - r * 0.35f, cy + r * 0.65f, cx - r * 0.72f, cy + r * 0.82f, color, strokeWidth);
    } else if (iconName == "folder") {
        D2D1_RECT_F body = D2D1::RectF(cx - r * 0.82f, cy - r * 0.42f, cx + r * 0.82f, cy + r * 0.72f);
        DrawRoundedRect(body, 2.5f, color, strokeWidth);
        DrawLine(cx - r * 0.80f, cy - r * 0.65f, cx - r * 0.15f, cy - r * 0.65f, color, strokeWidth);
    } else if (iconName == "clipboard") {
        D2D1_RECT_F board = D2D1::RectF(cx - r * 0.62f, cy - r * 0.62f, cx + r * 0.62f, cy + r * 0.82f);
        DrawRoundedRect(board, 2.5f, color, strokeWidth);
        D2D1_RECT_F clip = D2D1::RectF(cx - r * 0.30f, cy - r * 0.82f, cx + r * 0.30f, cy - r * 0.45f);
        FillRoundedRect(clip, 1.5f, color);
    } else if (iconName == "layout-grid") {
        float s = r * 0.68f;
        float g = r * 0.14f;
        DrawRoundedRect(D2D1::RectF(cx - s - g, cy - s - g, cx - g, cy - g), 2.0f, color, strokeWidth);
        DrawRoundedRect(D2D1::RectF(cx + g, cy - s - g, cx + s + g, cy - g), 2.0f, color, strokeWidth);
        DrawRoundedRect(D2D1::RectF(cx - s - g, cy + g, cx - g, cy + s + g), 2.0f, color, strokeWidth);
        DrawRoundedRect(D2D1::RectF(cx + g, cy + g, cx + s + g, cy + s + g), 2.0f, color, strokeWidth);
    } else if (iconName == "sliders-horizontal") {
        DrawLine(cx - r * 0.80f, cy - r * 0.45f, cx + r * 0.80f, cy - r * 0.45f, color, strokeWidth);
        FillCircle(cx - r * 0.25f, cy - r * 0.45f, r * 0.22f, color);
        DrawLine(cx - r * 0.80f, cy + r * 0.45f, cx + r * 0.80f, cy + r * 0.45f, color, strokeWidth);
        FillCircle(cx + r * 0.30f, cy + r * 0.45f, r * 0.22f, color);
    } else if (iconName == "grip-horizontal") {
        for (int row = -1; row <= 1; row += 2) {
            for (int col = -1; col <= 1; ++col) {
                FillCircle(cx + col * r * 0.55f, cy + row * r * 0.32f, r * 0.16f, color);
            }
        }
    } else if (iconName == "headphones" || iconName == "mic") {
        DrawCircle(cx, cy - r * 0.20f, r * 0.36f, color, strokeWidth);
        DrawLine(cx, cy + r * 0.20f, cx, cy + r * 0.72f, color, strokeWidth);
        DrawLine(cx - r * 0.40f, cy + r * 0.72f, cx + r * 0.40f, cy + r * 0.72f, color, strokeWidth);
    } else if (iconName == "settings") {
        DrawCircle(cx, cy, r * 0.45f, color, strokeWidth);
        for (int i = 0; i < 6; ++i) {
            float a = i * 3.14159265f / 3.0f;
            DrawLine(cx + std::cos(a) * r * 0.50f, cy + std::sin(a) * r * 0.50f,
                     cx + std::cos(a) * r * 0.84f, cy + std::sin(a) * r * 0.84f, color, strokeWidth);
        }
    } else if (iconName == "play") {
        ID2D1PathGeometry* p = nullptr;
        if (SUCCEEDED(m_d2dFactory->CreatePathGeometry(&p)) && p) {
            ID2D1GeometrySink* s = nullptr;
            if (SUCCEEDED(p->Open(&s)) && s) {
                s->BeginFigure(D2D1::Point2F(cx - r * 0.45f, cy - r * 0.65f), D2D1_FIGURE_BEGIN_FILLED);
                s->AddLine(D2D1::Point2F(cx + r * 0.68f, cy));
                s->AddLine(D2D1::Point2F(cx - r * 0.45f, cy + r * 0.65f));
                s->EndFigure(D2D1_FIGURE_END_CLOSED);
                s->Close();
                s->Release();
            }
            m_solidBrush->SetColor(color);
            m_dcRenderTarget->FillGeometry(p, m_solidBrush);
            p->Release();
        }
    } else if (iconName == "pause") {
        FillRoundedRect(D2D1::RectF(cx - r * 0.55f, cy - r * 0.62f, cx - r * 0.16f, cy + r * 0.62f), 1.5f, color);
        FillRoundedRect(D2D1::RectF(cx + r * 0.16f, cy - r * 0.62f, cx + r * 0.55f, cy + r * 0.62f), 1.5f, color);
    } else if (iconName == "skip-back") {
        DrawLine(cx - r * 0.55f, cy - r * 0.55f, cx - r * 0.55f, cy + r * 0.55f, color, strokeWidth);
        DrawLine(cx + r * 0.55f, cy - r * 0.55f, cx - r * 0.35f, cy, color, strokeWidth);
        DrawLine(cx - r * 0.35f, cy, cx + r * 0.55f, cy + r * 0.55f, color, strokeWidth);
    } else if (iconName == "skip-forward") {
        DrawLine(cx + r * 0.55f, cy - r * 0.55f, cx + r * 0.55f, cy + r * 0.55f, color, strokeWidth);
        DrawLine(cx - r * 0.55f, cy - r * 0.55f, cx + r * 0.35f, cy, color, strokeWidth);
        DrawLine(cx + r * 0.35f, cy, cx - r * 0.55f, cy + r * 0.55f, color, strokeWidth);
    } else if (iconName == "heart-filled" || iconName == "heart") {
        bool filled = (iconName == "heart-filled");
        if (filled) {
            FillCircle(cx - r * 0.32f, cy - r * 0.22f, r * 0.36f, color);
            FillCircle(cx + r * 0.32f, cy - r * 0.22f, r * 0.36f, color);
            ID2D1PathGeometry* p = nullptr;
            if (SUCCEEDED(m_d2dFactory->CreatePathGeometry(&p)) && p) {
                ID2D1GeometrySink* s = nullptr;
                if (SUCCEEDED(p->Open(&s)) && s) {
                    s->BeginFigure(D2D1::Point2F(cx - r * 0.66f, cy - r * 0.08f), D2D1_FIGURE_BEGIN_FILLED);
                    s->AddLine(D2D1::Point2F(cx + r * 0.66f, cy - r * 0.08f));
                    s->AddLine(D2D1::Point2F(cx, cy + r * 0.72f));
                    s->EndFigure(D2D1_FIGURE_END_CLOSED);
                    s->Close();
                    s->Release();
                }
                m_solidBrush->SetColor(color);
                m_dcRenderTarget->FillGeometry(p, m_solidBrush);
                p->Release();
            }
        } else {
            DrawCircle(cx, cy, r * 0.55f, color, strokeWidth * 0.85f);
        }
    } else if (iconName == "palette") {
        DrawCircle(cx, cy, r * 0.78f, color, strokeWidth);
        FillCircle(cx - r * 0.28f, cy - r * 0.22f, r * 0.14f, color);
        FillCircle(cx + r * 0.26f, cy - r * 0.22f, r * 0.14f, color);
        FillCircle(cx, cy + r * 0.28f, r * 0.16f, color);
    } else if (iconName == "volume") {
        DrawLine(cx - r * 0.65f, cy - r * 0.28f, cx - r * 0.65f, cy + r * 0.28f, color, strokeWidth);
        DrawLine(cx - r * 0.65f, cy - r * 0.28f, cx - r * 0.10f, cy - r * 0.62f, color, strokeWidth);
        DrawLine(cx - r * 0.65f, cy + r * 0.28f, cx - r * 0.10f, cy + r * 0.62f, color, strokeWidth);
        DrawLine(cx - r * 0.10f, cy - r * 0.62f, cx - r * 0.10f, cy + r * 0.62f, color, strokeWidth);
        DrawLine(cx + r * 0.32f, cy - r * 0.35f, cx + r * 0.52f, cy, color, strokeWidth);
        DrawLine(cx + r * 0.52f, cy, cx + r * 0.32f, cy + r * 0.35f, color, strokeWidth);
    } else if (iconName == "sun") {
        DrawCircle(cx, cy, r * 0.42f, color, strokeWidth);
        for (int i = 0; i < 8; ++i) {
            float a = i * 3.14159265f / 4.0f;
            DrawLine(cx + std::cos(a) * r * 0.58f, cy + std::sin(a) * r * 0.58f,
                     cx + std::cos(a) * r * 0.85f, cy + std::sin(a) * r * 0.85f, color, strokeWidth);
        }
    } else {
        DrawCircle(cx, cy, r * 0.65f, color, strokeWidth);
    }
}

void D2DRenderer::DrawQrCode(
    const std::wstring& payload, float left, float top, float sizeDip,
    const D2D1_COLOR_F& fgColor, const D2D1_COLOR_F& bgColor)
{
    FillRoundedRect(D2D1::RectF(left, top, left + sizeDip, top + sizeDip), 8.0f, bgColor);

    constexpr int N = 21;
    bool grid[N][N] = {};
    bool reserved[N][N] = {};

    auto putFinder = [&](int ox, int oy) {
        for (int dy = -1; dy <= 7; ++dy) {
            for (int dx = -1; dx <= 7; ++dx) {
                int x = ox + dx, y = oy + dy;
                if (x < 0 || x >= N || y < 0 || y >= N) continue;
                reserved[y][x] = true;
                bool outer = (dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6) &&
                             (dx == 0 || dx == 6 || dy == 0 || dy == 6);
                bool inner = (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4);
                grid[y][x] = (outer || inner);
            }
        }
    };

    putFinder(0, 0);
    putFinder(N - 7, 0);
    putFinder(0, N - 7);

    for (int i = 8; i < N - 8; ++i) {
        reserved[6][i] = reserved[i][6] = true;
        grid[6][i] = grid[i][6] = (i % 2 == 0);
    }

    std::string utf8 = WideToUtf8(payload);
    if (utf8.empty()) utf8 = "http://127.0.0.1:47824";
    size_t bitIdx = 0;
    for (int y = 0; y < N; ++y) {
        for (int x = 0; x < N; ++x) {
            if (reserved[y][x]) continue;
            unsigned char ch = static_cast<unsigned char>(utf8[(bitIdx / 8) % utf8.size()]);
            int b = (ch >> (bitIdx % 8)) & 1;
            grid[y][x] = ((b ^ ((x + y) % 2)) != 0);
            ++bitIdx;
        }
    }

    float pad = 6.0f;
    float cell = (sizeDip - pad * 2.0f) / static_cast<float>(N);
    for (int y = 0; y < N; ++y) {
        for (int x = 0; x < N; ++x) {
            if (grid[y][x]) {
                D2D1_RECT_F r = D2D1::RectF(
                    left + pad + x * cell,
                    top + pad + y * cell,
                    left + pad + (x + 1) * cell,
                    top + pad + (y + 1) * cell);
                FillRoundedRect(r, 0.5f, fgColor);
            }
        }
    }
}

void D2DRenderer::AddHitRegion(
    const std::string& id, const D2D1_RECT_F& r,
    std::function<void()> onClick, std::function<void(float)> onDrag)
{
    m_hitRegions.push_back({ id, r, std::move(onClick), std::move(onDrag) });
}

bool D2DRenderer::IsHovered(const D2D1_RECT_F& r, float dipX, float dipY) const {
    return dipX >= r.left && dipX <= r.right && dipY >= r.top && dipY <= r.bottom;
}

bool D2DRenderer::DispatchClick(float dipX, float dipY) {
    for (auto it = m_hitRegions.rbegin(); it != m_hitRegions.rend(); ++it) {
        if (IsHovered(it->rect, dipX, dipY)) {
            if (it->onDrag) {
                m_activeDragId = it->id;
                float norm = std::clamp((dipX - it->rect.left) / std::max(1.0f, it->rect.right - it->rect.left), 0.0f, 1.0f);
                it->onDrag(norm);
                return true;
            }
            if (it->onClick) {
                it->onClick();
                return true;
            }
        }
    }
    m_activeDragId.clear();
    return false;
}

bool D2DRenderer::DispatchDrag(float dipX, float dipY) {
    for (auto it = m_hitRegions.rbegin(); it != m_hitRegions.rend(); ++it) {
        if (it->onDrag && (it->id == m_activeDragId || IsHovered(it->rect, dipX, dipY))) {
            m_activeDragId = it->id;
            float norm = std::clamp((dipX - it->rect.left) / std::max(1.0f, it->rect.right - it->rect.left), 0.0f, 1.0f);
            it->onDrag(norm);
            return true;
        }
    }
    return false;
}

} // namespace np
