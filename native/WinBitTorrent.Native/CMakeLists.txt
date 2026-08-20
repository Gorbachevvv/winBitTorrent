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
endif()

install(TARGETS WinBitTorrent.Native
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .
    ARCHIVE DESTINATION lib
)
