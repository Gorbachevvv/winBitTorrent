#include "engine_api.hpp"

#include <libtorrent/version.hpp>

#include <algorithm>
#include <cstdlib>
#include <exception>
#include <memory>
#include <stdexcept>
#include <string>
#include <cstring>

#if defined(_WIN32)
#define WBT_EXPORT extern "C" __declspec(dllexport)
#else
#define WBT_EXPORT extern "C" __attribute__((visibility("default")))
#endif

WBT_EXPORT char const* wbt_libtorrent_version() noexcept
{
    return libtorrent::version();
}

namespace
{
    char* copy_string(std::string const& value)
    {
        auto* result = static_cast<char*>(std::malloc(value.size() + 1));
        if (result == nullptr) return nullptr;
        std::copy(value.begin(), value.end(), result);
        result[value.size()] = '\0';
        return result;
    }
}

WBT_EXPORT void* wbt_engine_create(char const* data_root, char** error) noexcept
{
    if (error != nullptr) *error = nullptr;
    try
    {
        if (data_root == nullptr) throw std::invalid_argument("data_root is required");
        return winbittorrent::create_engine(data_root);
    }
    catch (std::exception const& exception)
    {
        if (error != nullptr) *error = copy_string(exception.what());
        return nullptr;
    }
}

WBT_EXPORT int wbt_engine_invoke(void* handle, char const* method, char const* payload, char** response, char** error) noexcept
{
    if (response != nullptr) *response = nullptr;
    if (error != nullptr) *error = nullptr;
    try
    {
        if (handle == nullptr || method == nullptr) throw std::invalid_argument("engine handle and method are required");
        auto result = winbittorrent::invoke_engine(
            *static_cast<winbittorrent::engine*>(handle), method, payload == nullptr ? "{}" : payload);
        if (response != nullptr) *response = copy_string(result);
        return 0;
    }
    catch (std::exception const& exception)
    {
        if (error != nullptr) *error = copy_string(exception.what());
        return 1;
    }
}

WBT_EXPORT void wbt_engine_destroy(void* handle) noexcept
{
    winbittorrent::destroy_engine(static_cast<winbittorrent::engine*>(handle));
}

WBT_EXPORT void wbt_string_free(char* value) noexcept
{
    std::free(value);
}

using wbt_progress_callback = int(*)(int completed, int total, void* context);

WBT_EXPORT int wbt_create_torrent(
    char const* payload,
    wbt_progress_callback progress,
    void* context,
    unsigned char** data,
    std::size_t* size,
    char** error) noexcept
{
    if (data != nullptr) *data = nullptr;
    if (size != nullptr) *size = 0;
    if (error != nullptr) *error = nullptr;
    try
    {
        if (payload == nullptr || data == nullptr || size == nullptr) throw std::invalid_argument("payload, data and size are required");
        auto result = winbittorrent::create_torrent_data(payload, [=](int completed, int total)
        {
            return progress == nullptr || progress(completed, total, context) != 0;
        });
        auto* buffer = static_cast<unsigned char*>(std::malloc(result.size()));
        if (buffer == nullptr && !result.empty()) throw std::bad_alloc();
        if (!result.empty()) std::memcpy(buffer, result.data(), result.size());
        *data = buffer;
        *size = result.size();
        return 0;
    }
    catch (std::exception const& exception)
    {
        if (error != nullptr) *error = copy_string(exception.what());
        return 1;
    }
}

WBT_EXPORT void wbt_bytes_free(void* value) noexcept
{
    std::free(value);
}
