cmake_minimum_required(VERSION 3.20)
project(OpenPixel VERSION 0.5.126 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_definitions(-DUNICODE -D_UNICODE -DWIN32_LEAN_AND_MEAN -DNOMINMAX -D_WIN32_WINNT=0x0A00)

set(SOURCES
    src/main.cpp
    src/core/ConfigManager.cpp
    src/render/D2DRenderer.cpp
    src/ui/IslandRendererTabs.cpp
    src/ui/TaskbarWindow.cpp
    src/ui/DesktopWidgetsWindow.cpp
    src/ui/DrawersWindow.cpp
    src/ui/SettingsWindow.cpp
    src/services/SystemServices.cpp
    src/services/NetworkServers.cpp
)

add_executable(OpenPixel WIN32 ${SOURCES})

target_include_directories(OpenPixel PRIVATE src)

if(MINGW)
    target_compile_options(OpenPixel PRIVATE --specs=${CMAKE_CURRENT_SOURCE_DIR}/build_specs -O2 -Wall -Wno-unused-parameter -Wno-missing-field-initializers -municode)
    target_link_options(OpenPixel PRIVATE --specs=${CMAKE_CURRENT_SOURCE_DIR}/build_specs -municode -static-libgcc -static-libstdc++)
endif()

target_link_libraries(OpenPixel PRIVATE
    d2d1
    dwrite
    d3d11
    dxgi
    dwmapi
    gdiplus
    uxtheme
    msimg32
    winmm
    iphlpapi
    powrprof
    dxva2
    ws2_32
    winhttp
    wbemuuid
    crypt32
    bcrypt
    ole32
    oleaut32
    uuid
    shell32
    shlwapi
    comctl32
    comdlg32
    userenv
    psapi
)
