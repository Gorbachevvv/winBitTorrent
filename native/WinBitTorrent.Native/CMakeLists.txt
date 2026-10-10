cmake_minimum_required(VERSION 3.25)
project(WinBitTorrentNative VERSION 1.0.0 LANGUAGES CXX)

find_package(LibtorrentRasterbar CONFIG REQUIRED)
find_package(Boost CONFIG REQUIRED COMPONENTS json)

add_library(WinBitTorrent.Native SHARED
    src/engine_api.cpp
    src/native_api.cpp
)

target_compile_features(WinBitTorrent.Native PRIVATE cxx_std_20)
target_compile_definitions(WinBitTorrent.Native PRIVATE
    WINBITTORRENT_NATIVE_EXPORTS
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)
target_link_libraries(WinBitTorrent.Native PRIVATE
    LibtorrentRasterbar::torrent-rasterbar
    Boost::json
)

if(MSVC)
    target_compile_options(WinBitTorrent.Native PRIVATE /W4 /permissive- /utf-8)
    # The worker loads this bridge in its own process and must have the matching
    # C++ runtime beside it, even when only the WinUI app carries its own runtime.
    set(CMAKE_INSTALL_SYSTEM_RUNTIME_DESTINATION .)
    set(CMAKE_INSTALL_SYSTEM_RUNTIME_LIBS_SKIP TRUE)
    include(InstallRequiredSystemLibraries)
    if(MSVC_CXX_ARCHITECTURE_ID STREQUAL "ARM64")
        # The ARM64 redist also contains this x64 exception helper. Native ARM64
        # imports VCRUNTIME140.dll and does not use VCRUNTIME140_1.dll.
        list(FILTER CMAKE_INSTALL_SYSTEM_RUNTIME_LIBS EXCLUDE REGEX "/vcruntime140_1\\.dll$")
    endif()
    install(PROGRAMS ${CMAKE_INSTALL_SYSTEM_RUNTIME_LIBS} DESTINATION .)
endif()

install(TARGETS WinBitTorrent.Native
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .
    ARCHIVE DESTINATION lib
)
